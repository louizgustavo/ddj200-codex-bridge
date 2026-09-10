using System.Text.Json;

namespace Ddj200;

public record AnalogIntent(string Target,string Method,string? Key=null,int? Act=null,double? Angle=null,double? Distance=null,string? Reason=null)
{
    public object Wire()=>Method switch
    {
        "v.oai.hid"=>new{method=Method,@params=new{k=Key,act=Act}},
        "v.oai.rad"=>(object)new{method=Method,@params=new{a=Angle,d=Distance}},
        _=>throw new InvalidOperationException("Discarded motion has no wire event")
    };
}
public static class AnalogSurfaceMap
{
    public static readonly Dictionary<string,string> Expected=new()
    {
        ["ENC_CC"]="cc:1:33:negative",["ENC_CW"]="cc:1:33:positive",
        ["encoder.click"]="note:1:54",["encoder.longPress"]="note:1:54",
        ["joystick.left"]="cc:2:33:negative",["joystick.right"]="cc:2:33:positive",
        ["joystick.up"]="absolute14:2:0:negative_from_center",["joystick.down"]="absolute14:2:0:positive_from_center"
    };
    public static void Validate(bool allowNativeRemapping=false)
    {
        var found=ProductProfile.Load().Analogs;
        if(found.Count!=Expected.Count||Expected.Any(p=>!found.TryGetValue(p.Key,out var value)||p.Value!=value))throw new InvalidDataException("Analog contract differs from reviewed physical captures");
        if(Surface12Map.Load().Any(x=>Expected.Values.Contains(x.Signature)))throw new InvalidDataException("Analog/button physical conflict");
        var layout=JsonSerializer.SerializeToElement(MicroBinding.ParseLayoutToml(File.ReadAllText(MicroBinding.StatePath)));
        if(allowNativeRemapping)
        {
            if(layout.GetProperty("encoderMode").ValueKind!=JsonValueKind.String)throw new InvalidDataException("Unsupported native encoder mode structure");
            foreach(string direction in new[]{"up","right","down","left"})
            {
                var action=layout.GetProperty("analogStick").GetProperty(direction);
                if(action.GetProperty("type").GetString()!="command"||string.IsNullOrWhiteSpace(action.GetProperty("commandId").GetString()))throw new InvalidDataException("Unsupported native joystick binding structure");
            }
            return;
        }
        if(layout.GetProperty("encoderMode").GetString()!="composer-navigation")throw new InvalidDataException("Encoder mode changed");
        var stick=layout.GetProperty("analogStick");
        foreach(var pair in new Dictionary<string,string>{{"up","composer.togglePlanMode"},{"right","navigateForward"},{"down","toggleSidebar"},{"left","navigateBack"}})
        {
            var action=stick.GetProperty(pair.Key);
            if(action.GetProperty("type").GetString()!="command"||action.GetProperty("commandId").GetString()!=pair.Value)throw new InvalidDataException("Joystick binding changed");
        }
    }
}

public sealed record JogSensitivity(double LeftScale = .25, double RightScale = .1)
{
    public static JogSensitivity Read()
    {
        var preset=ProductProfile.Load().Jog;
        return new JogSensitivity(preset.LeftScale,preset.RightScale).Validated();
    }
    public JogSensitivity Validated()
    {
        if (!double.IsFinite(LeftScale) || !double.IsFinite(RightScale) || LeftScale <= 0 || RightScale <= 0 || LeftScale > 1 || RightScale > 1)
            throw new InvalidDataException("Jog scales must be finite and in (0, 1]");
        return this;
    }
}

// Clock-injected MIDI intent policy. No hardware, UI, timers or backlogged movement.
public sealed class AnalogSurfaceLogic
{
    public const int LeftIntervalMs = 50;
    private readonly JogSensitivity sensitivity;
    private readonly double[] motion = new double[2];
    private readonly int[] motionDirection = new int[2];
    private readonly long[] motionAt = {-1000,-1000};
    public AnalogSurfaceLogic(JogSensitivity? sensitivity = null) => this.sensitivity = (sensitivity ?? new()).Validated();
    private void ClearMotion(int deck) { motion[deck] = 0; motionDirection[deck] = 0; }
    private bool Quantize(int deck, int direction, long now)
    {
        // Left fractional distance has no timeout: slow movement must still count.
        // A fraction is not a queued action; only a fresh physical increment can emit.
        if ((deck == 1 && now - motionAt[deck] >= 180) || motionDirection[deck] != direction) ClearMotion(deck);
        motionAt[deck] = now; motionDirection[deck] = direction;
        // The previous policy treated each nonzero MIDI increment as one action,
        // regardless of velocity. Scale those same increments, without acceleration.
        motion[deck] += deck == 0 ? sensitivity.LeftScale : sensitivity.RightScale;
        if (motion[deck] < 1 - 1e-9) return false;
        // Left retains only a sub-action fraction, so scales such as .4 produce
        // two actions per five increments. There is never a queued whole action.
        motion[deck] = deck == 0 ? Math.Max(0, motion[deck] - 1) : 0;
        return true;
    }
    private readonly HashSet<int>[] touch={new(),new()};
    private readonly Decoder decoder=new();
    private bool stopped,encoderHeld;
    private long encoderAt=-1000,rightAt=-1000;
    private int rightDirection,tempoDirection,rightBurstDirection;
    public bool TempoCentered{get;private set;}
    public bool IsTouched(int deckIndex)=>touch[deckIndex].Count>0;
    private static AnalogIntent Neutral()=>new("joystick.neutral","v.oai.rad",Angle:0,Distance:0);
    private static AnalogIntent Direction(string direction)=>new("joystick."+direction,"v.oai.rad",Angle:direction switch{"up"=>.75,"down"=>.25,"left"=>.5,_=>0},Distance:1);
    public IReadOnlyList<AnalogIntent> Input(MidiMessage message,long now)
    {
        var result=new List<AnalogIntent>();if(stopped||message.Data1>127||message.Data2>127)return result;
        int channel=message.Status&15,type=message.Status&0xF0;
        if(channel is 0 or 1 && type is 0x80 or 0x90 && message.Data1 is 0x36 or 0x67)
        {
            bool down=type==0x90&&message.Data2>0;
            ClearMotion(channel);
            if(channel==1)rightBurstDirection=0;
            if(down)touch[channel].Add(message.Data1);else touch[channel].Remove(message.Data1);
            if(channel==0)
            {
                encoderAt=now-LeftIntervalMs;
                if(message.Data1==0x36&&down!=encoderHeld)
                {encoderHeld=down;result.Add(new("encoder.press","v.oai.hid","ENC_PRESS",down?1:0));}
            }
            else if(IsTouched(1)&&rightDirection!=0){rightDirection=0;result.Add(Neutral());}
            return result;
        }
        if(type==0xB0&&channel is 0 or 1&&message.Data1 is 0x21 or 0x22 or 0x23 or 0x29)
        {
            if(IsTouched(channel))return new[]{new AnalogIntent("deck"+(channel+1)+".jog","discard",Reason:"jog_touched")};
            if(message.Data1!=0x21)return result; // Only the explicitly learned rotation stream.
            int direction=Math.Sign(message.Data2-64);if(direction==0)return result;
            if(channel==0)
            {
                // Apply the requested left interval before movement scaling.
                if(motionDirection[0]!=direction)ClearMotion(0);
                if(now-encoderAt<LeftIntervalMs)return result;
                encoderAt=now;
                if(!Quantize(0,direction,now))return result;
                string key=direction>0?"ENC_CW":"ENC_CC";result.Add(new(key,"v.oai.hid",key,2));
            }
            else
            {
                if(tempoDirection!=0){ClearMotion(1);return new[]{new AnalogIntent("joystick.horizontal","discard",Reason:"other_axis_active")};}
                if(now-motionAt[1]>=180||rightBurstDirection!=direction)rightBurstDirection=0;
                if(rightDirection!=0&&rightDirection!=direction){rightDirection=0;result.Add(Neutral());}
                if(sensitivity.RightScale<1&&rightBurstDirection==direction){motionAt[1]=now;return result;}
                if(!Quantize(1,direction,now))return result;
                rightBurstDirection=direction;
                // Scaled jog motion cannot prolong a native held direction indefinitely.
                if(rightDirection==0 || sensitivity.RightScale==1)rightAt=now;
                if(rightDirection!=direction)
                {if(rightDirection!=0)result.Add(Neutral());rightDirection=direction;result.Add(Direction(direction>0?"right":"left"));}
            }
            return result;
        }
        if(type!=0xB0||channel!=1||message.Data1 is not (0 or 32))return result;
        var decoded=decoder.Decode(message,now);if(decoded==null)return result;
        int delta=decoded.Value-8192;
        if(!TempoCentered)
        {
            if(Math.Abs(delta)<=128)TempoCentered=true;
            else result.Add(new("joystick.vertical","discard",Reason:"tempo_requires_center"));
            return result;
        }
        if(Math.Abs(delta)<=256)
        {if(tempoDirection!=0){tempoDirection=0;result.Add(Neutral());}return result;}
        if(Math.Abs(delta)<512)return result;
        int sign=Math.Sign(delta);
        if(tempoDirection==sign)return result;
        if(tempoDirection!=0)
        {tempoDirection=0;TempoCentered=false;result.Add(Neutral());return result;}
        if(rightDirection!=0)return new[]{new AnalogIntent("joystick.vertical","discard",Reason:"other_axis_active")};
        tempoDirection=sign;result.Add(Direction(sign>0?"down":"up"));return result;
    }
    public IReadOnlyList<AnalogIntent> Tick(long now)
    {
        if(stopped)return Array.Empty<AnalogIntent>();
        if(now-motionAt[1]>=180)ClearMotion(1);
        if(now-motionAt[1]>=180)rightBurstDirection=0;
        if(rightDirection!=0&&now-rightAt>=180){rightDirection=0;return new[]{Neutral()};}
        return Array.Empty<AnalogIntent>();
    }
    public IReadOnlyList<AnalogIntent> Stop()
    {
        var result=new List<AnalogIntent>();
        if(encoderHeld)result.Add(new("encoder.press","v.oai.hid","ENC_PRESS",0));
        if(rightDirection!=0||tempoDirection!=0)result.Add(Neutral());
        stopped=true;encoderHeld=false;rightDirection=tempoDirection=rightBurstDirection=0;
        ClearMotion(0);ClearMotion(1);foreach(var item in touch)item.Clear();return result;
    }
}
