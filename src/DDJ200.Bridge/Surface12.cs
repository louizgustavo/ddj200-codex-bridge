using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Ddj200;

public record Surface12Binding(string Target, string Control, string Signature, string Provenance, string? CaptureId)
{
    public bool IsDirection => Surface12Map.DirectionTargets.Contains(Target);
    public bool IsTask => Surface12Map.TaskTargets.Contains(Target);
    public string WireKey => Target == "ACT10_ACT11" ? "ACT10" : Target;
    public MidiMessage Led(bool on) => LedPolicy.Targets[Control] with { Data2 = (byte)(on ? 127 : 0) };
}
public static class Surface12Map
{
    public static readonly string[] DirectionTargets = { "joystick.up", "joystick.down" };
    public static readonly string[] TaskTargets = { "AG00", "AG01", "AG02", "AG03", "AG04", "AG05" };
    public static readonly Dictionary<string, string> Commands = new() { ["ACT06"]="FAST", ["ACT07"]="APPR", ["ACT08"]="REJ", ["ACT09"]="SPLIT", ["ACT10_ACT11"]="MIC", ["ACT12"]="CODEX" };
    public static List<Surface12Binding> Load()
    {
        var result = ProductProfile.Load().Buttons.Select(entry => new Surface12Binding(entry.Target, entry.Control, entry.Signature, "product_preset", null)).ToList();
        Validate(result); return result.OrderBy(x => Array.IndexOf(TaskTargets.Concat(Commands.Keys).Concat(DirectionTargets).ToArray(), x.Target)).ToList();
    }
    public static void Validate(IReadOnlyList<Surface12Binding> map)
    {
        if (map.Count != 14 || map.Select(x => x.Target).Distinct().Count() != 14 || map.Select(x => x.Signature).Distinct().Count() != 14 ||
            !map.Select(x => x.Target).ToHashSet().SetEquals(TaskTargets.Concat(Commands.Keys).Concat(DirectionTargets))) throw new InvalidDataException("Exactly 14 unique requested targets/physical controls required");
        foreach (var item in map)
        {
            if (!LedPolicy.Targets.TryGetValue(item.Control, out var note) || !LedPolicy.IsAllowed(note) ||
                item.Signature != $"note:{(note.Status & 15)+1}:{note.Data1}") throw new InvalidDataException("Physical identity disagrees with verified decoder/LED inventory");
        }
    }
    public static string ConfigurationFingerprint(bool nativeRemapping=false)=>ConfigurationFingerprint(File.ReadAllText(MicroBinding.StatePath),nativeRemapping);
    public static string ConfigurationFingerprint(string configText,bool nativeRemapping=false)
    {
        var layout = JsonSerializer.SerializeToElement(MicroBinding.ParseLayoutToml(configText));
        if (layout.GetProperty("version").GetInt32() != 1 || layout.GetProperty("separateMicrophoneKeys").GetBoolean() || layout.GetProperty("voiceButtonMode").GetString() != "push-to-talk") throw new InvalidDataException("Micro layout mode changed");
        var slots = layout.GetProperty("slots");
        foreach (var command in Commands)
        {
            var binding = slots.GetProperty(command.Key);
            if(nativeRemapping)
            {
                if(binding.ValueKind!=JsonValueKind.Object ||
                    !binding.TryGetProperty("keycapId",out var cap)||cap.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(cap.GetString())||
                    binding.EnumerateObject().Any(p=>p.Name is not ("keycapId" or "commandId")||p.Value.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(p.Value.GetString())))
                    throw new InvalidDataException("Unsupported native key binding structure");
            }
            else if (binding.EnumerateObject().Count() != 1 || binding.GetProperty("keycapId").GetString() != command.Value) throw new InvalidDataException("Command binding changed");
        }
        if(nativeRemapping)
        {
            ReadTaskSource(configText);
            // The bridge emits physical Micro key identities; Codex owns their
            // configured actions. Function changes do not alter the wire contract.
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
                version=layout.GetProperty("version").GetInt32(),
                separateMicrophoneKeys=layout.GetProperty("separateMicrophoneKeys").GetBoolean(),
                voiceButtonMode=layout.GetProperty("voiceButtonMode").GetString()
            }))));
        }
        return MicroConfigurationFingerprint(configText);
    }
    // Guard all Micro settings, including source/focus, without coupling the bridge
    // lifetime to unrelated Codex edits such as font size or model selection.
    public static string MicroConfigurationFingerprint(string configText)
    {
        var relevant=new List<string>();string section="";
        foreach(string raw in configText.Split('\n'))
        {
            string line=raw.Trim();if(line.Length==0||line.StartsWith('#'))continue;
            if(line.StartsWith('['))section=line;
            if(section.StartsWith("[desktop.codex-micro-") ||
                section=="[desktop]"&&line.StartsWith("codex-micro-"))relevant.Add(line);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',relevant))));
    }
    public static string ReadTaskSource(string configText)
    {
        string section="";string? source=null;int matches=0;
        foreach(string raw in configText.Split('\n'))
        {
            string line=raw.Trim();if(line.StartsWith('[')){section=line;continue;}
            if(section!="[desktop]"||!line.StartsWith("codex-micro-agent-source"))continue;
            int split=line.IndexOf('=');
            if(split<0||line[..split].Trim()!="codex-micro-agent-source")throw new InvalidDataException("Invalid Micro task source assignment");
            source=JsonSerializer.Deserialize<string>(line[(split+1)..].Trim());
            if(source is not ("recent" or "pinned"))throw new InvalidDataException("Micro task source must be recent or pinned");
            matches++;
        }
        if(matches!=1)throw new InvalidDataException("Exactly one explicit Micro task source required");
        return source!;
    }
    public static void ValidateTaskReview(JsonElement review,DateTimeOffset now,string taskSource="recent")
    {
        if(taskSource is not ("recent" or "pinned") || review.GetProperty("source").GetString()!="live_Creator_Micro_accessibility_and_visual_order" || review.GetProperty("agentSource").GetString()!=taskSource) throw new InvalidDataException("Task review must match the selected Micro source");
        var age=now-review.GetProperty("reviewedAt").GetDateTimeOffset();
        if(age<TimeSpan.Zero||age>TimeSpan.FromMinutes(10))throw new InvalidDataException("Task identity review expired; refresh current preview");
        var entries=review.GetProperty("slots").EnumerateArray().ToArray();
        if(entries.Length!=6 || !entries.Select(x=>x.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0,6)))throw new InvalidDataException("Review requires six unique slot positions");
        foreach(var entry in entries)
            if(entry.GetProperty("target").GetString()!=TaskTargets[entry.GetProperty("id").GetInt32()]||string.IsNullOrWhiteSpace(entry.GetProperty("title").GetString()))throw new InvalidDataException("Missing current task title or mismatched position");
    }
}

public record SurfaceTaskState(string Status, bool Selected, bool Lit = true);
public record SurfaceInput(Surface12Binding Binding, int Act, long HeldMs);
public sealed class Surface12Logic(IReadOnlyList<Surface12Binding> bindings)
{
    public const int SelectedOnMs=2000, SelectedOffMs=100;
    private readonly Dictionary<string, long> held = new();
    private readonly Dictionary<string, long> released = new();
    private SurfaceTaskState[]? tasks;
    private readonly long[] phase = new long[6];
    private long feedbackAt = -1;
    private bool stopped;
    private readonly IReadOnlyDictionary<string,ProductLedPattern> ledPatterns = ProductProfile.Load().EffectiveLedPatterns;
    public bool Ready(long now, bool connected) => !stopped && connected && tasks != null && feedbackAt >= 0 && now - feedbackAt is >= 0 and <= 75000;
    public IReadOnlyList<SurfaceTaskState>? Tasks => tasks;
    public void CancelTaskGestures()
    {
        foreach(string target in Surface12Map.TaskTargets){held.Remove(target);released.Remove(target);}
    }
    public void Receive(string method, JsonElement value, long now)
    {
        if (stopped) return;
        if (method is "device.status" or "v.oai.rgbcfg") { feedbackAt = now; return; }
        if (method != "v.oai.thstatus") return;
        var next = ParseTasks(value);
        for (int i=0;i<6;i++) if (tasks == null || tasks[i] != next[i]) phase[i] = now;
        tasks = next; feedbackAt = now;
    }
    public static SurfaceTaskState[] ParseTasks(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 6) throw new InvalidDataException("Expected complete six-slot real lighting snapshot");
        var states = new SurfaceTaskState[6];
        foreach (var entry in value.EnumerateArray())
        {
            int id=entry.GetProperty("id").GetInt32(), c=entry.GetProperty("c").GetInt32(), e=entry.GetProperty("e").GetInt32();
            double b=entry.GetProperty("b").GetDouble(), s=entry.GetProperty("s").GetDouble();
            if (id is <0 or >5 || states[id]!=null || !double.IsFinite(b) || b is <0 or >1 || !double.IsFinite(s) ||
                entry.GetProperty("sk").GetInt32()!=0 || entry.GetProperty("sa").GetInt32()!=0) throw new InvalidDataException("Invalid or duplicate thread lighting");
            if (e==0 && c==0 && b==0 && s==0) { states[id]=new("off",false,false); continue; }
            if (e is not (1 or 4) || (e==1 && s!=0) || (e==4 && Math.Abs(s-0.4)>0.00001)) throw new InvalidDataException("Unknown task lighting effect");
            string state=c switch { 3166206=>"working",65356=>"unread",16777215=>"idle",16739584=>"attention",16711731=>"error",_=>throw new InvalidDataException("Unknown task color; no state guessed") };
            // Installed normal task producer sets no pulsing field; service emits breath for selected.
            states[id]=new(state,e==4,b>0);
        }
        if (states.Count(x=>x.Selected)>1) throw new InvalidDataException("Ambiguous selection/preview; disarmed");
        return states;
    }
    public static bool TaskLed(SurfaceTaskState state,long elapsed)
    {
        string key = !state.Lit || state.Status=="off" ? "unassigned" : state.Status is "attention" or "error" ? state.Status : state.Selected ? "selected" : state.Status;
        return ProductLedPolicy.IsOn(ProductLedPolicy.Default[key], elapsed);
    }
    public bool Led(Surface12Binding binding,long now,bool connected)
    {
        if (!Ready(now,connected)) return false;
        if (binding.IsTask)
        {
            int i=Array.IndexOf(Surface12Map.TaskTargets,binding.Target);
            var state=tasks![i];
            string key=!state.Lit || state.Status=="off" ? "unassigned" : state.Status is "attention" or "error" ? state.Status : state.Selected ? "selected" : state.Status;
            return ProductLedPolicy.IsOn(ledPatterns[key],now-phase[i]);
        }
        return held.TryGetValue(binding.Target,out long start)
            ? ProductLedPolicy.IsOn(ledPatterns["commandHold"],now-start)
            : released.ContainsKey(binding.Target)
                ? ProductLedPolicy.IsOn(ledPatterns["commandReleased"],now-released[binding.Target])
                : ProductLedPolicy.IsOn(ledPatterns["commandConfigured"],now);
    }
    public SurfaceInput? Input(MidiMessage message,long now,bool connected)
    {
        if (!Ready(now,connected) || (message.Status&0xF0) is not (0x80 or 0x90) || message.Data2>127) return null;
        string signature=$"note:{(message.Status&15)+1}:{message.Data1}";
        var binding=bindings.SingleOrDefault(x=>x.Signature==signature);if(binding==null)return null;
        bool down=(message.Status&0xF0)==0x90&&message.Data2>0;
        if(down)
        {
            if(held.ContainsKey(binding.Target) || (released.TryGetValue(binding.Target,out long end)&&now-end<30))return null;
            held.Add(binding.Target,now);return new(binding,1,0);
        }
        if(!held.Remove(binding.Target,out long start))return null;
        released[binding.Target]=now;return new(binding,0,now-start);
    }
    public void Stop() { stopped=true;held.Clear();tasks=null; }
}

public static class Surface12Run
{
    public static HashSet<string> EnabledCommands(string? requested)
    {
        var targets=(requested??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet();
        if(targets.Any(t=>!Surface12Map.Commands.ContainsKey(t)))throw new ArgumentException("Only explicit configured command targets may be enabled");
        return targets;
    }
    public static bool ShouldSendCommand(SurfaceInput edge,IReadOnlySet<string> enabled)=>!edge.Binding.IsTask && enabled.Contains(edge.Binding.Target) && edge.Act is 0 or 1;
    public static TimeSpan SessionTimeout(int? seconds,bool arm)
    {
        if(!arm || seconds is <30 or >600)throw new ArgumentException("Explicit --arm and 30..600 second test window, or continuous mode, required");
        return seconds.HasValue?TimeSpan.FromSeconds(seconds.Value):Timeout.InfiniteTimeSpan;
    }
    public static async Task<int> Run(int? seconds,bool arm,bool allowTaskSelection,string? enableCommands=null,bool observeAnalogs=false,bool executeAnalogs=false,string transport="usb",ulong? bleAddress=null,string? bleName=null)
    {
        var timeout=SessionTimeout(seconds,arm);
        var startedAt=DateTimeOffset.UtcNow;
        DateTimeOffset? expiresAt=seconds.HasValue?startedAt.AddSeconds(seconds.Value):null;
        var commands=EnabledCommands(enableCommands);
        observeAnalogs|=executeAnalogs;
        if(observeAnalogs)AnalogSurfaceMap.Validate(allowNativeRemapping:!seconds.HasValue);
        var jogSensitivity=observeAnalogs?JogSensitivity.Read():null;
        var analog=observeAnalogs?new AnalogSurfaceLogic(jogSensitivity):null;
        string configText=File.ReadAllText(MicroBinding.StatePath);
        var map=Surface12Map.Load();string config=Surface12Map.ConfigurationFingerprint(configText,nativeRemapping:!seconds.HasValue);
        string taskSource=Surface12Map.ReadTaskSource(configText);
        string mapHash=HashInputs(observeAnalogs);
        string folder=ProductProfile.RuntimeFolder;
        Directory.CreateDirectory(folder);
        if(File.Exists(folder+"/stop"))throw new InvalidOperationException("Remove explicit stop marker before arming");
        // Test windows review current identities. Continuous use follows the user's
        // selected native slot positions, rather than caching task identities/order.
        if(allowTaskSelection && seconds.HasValue)
        {
            using var review=JsonDocument.Parse(File.ReadAllText(folder+"/task-bindings-reviewed.json"));
            Surface12Map.ValidateTaskReview(review.RootElement,DateTimeOffset.UtcNow,taskSource);
        }
        using var owner=new DeviceOwnership();
        if(transport is not ("usb" or "bluetooth"))throw new ArgumentException("Transport must be usb or bluetooth");
        if(transport=="bluetooth"&&!bleAddress.HasValue)throw new ArgumentException("Bluetooth requires an explicitly selected DDJ-200 address");
        MidiLearn.Save(folder+"/status.json",new{state=transport=="bluetooth"?"connecting":"starting",detail=(string?)null,pid=Environment.ProcessId,time=DateTimeOffset.UtcNow,startedAt,transport=transport=="usb"?"USB":"Bluetooth",connectionState=transport=="bluetooth"?"connecting":"starting"});
        var queue=Channel.CreateBounded<(string Method,JsonElement Value,long Time)>(128);int overflow=0;
        var clock=Stopwatch.StartNew();var logic=new Surface12Logic(map);
        await using var server=new MicroUsbIp();
        server.LightingReceived+=(method,value)=>{if(!queue.Writer.TryWrite((method,value,clock.ElapsedMilliseconds)))Interlocked.Exchange(ref overflow,1);};
        ISurfaceMidi midi;
        try
        {
            midi=transport=="usb"
                ? new MidiDevice(MidiDevice.Select(MidiDevice.Ports(false),"DDJ-200"),MidiDevice.Select(MidiDevice.Ports(true),"DDJ-200"))
                : await ConnectBluetoothAsync(bleAddress!.Value,bleName);
        }
        catch(Exception e)
        {
            MidiLearn.Save(folder+"/status.json",new{state="error",detail=e.Message,pid=Environment.ProcessId,time=DateTimeOffset.UtcNow,startedAt,transport=transport=="usb"?"USB":"Bluetooth",connectionState="error"});throw;
        }
        using(midi)
        {
        using var log=new BoundedEventLog(folder+"/logs");
        var last=map.ToDictionary(x=>x.Target,_=>false);var emittedHeld=new HashSet<string>();bool imported=false,active=false,encoderDown=false,joystickDisplaced=false;long guard=0;string? failure=null;
        using var stop=new CancellationTokenSource(timeout);
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=handler;
        bool logUnavailable=false;
        void Log(object o)
        {
            if(logUnavailable)return;
            try{log.WriteLine(JsonSerializer.Serialize(o,MidiLearn.Json).Replace("\r","").Replace("\n",""));}
            catch(Exception e)
            {
                logUnavailable=true;failure??="Event log unavailable: "+e.Message;
                Console.Error.WriteLine(failure);stop.Cancel();
                // Keep releasing held controls and switching off every owned LED.
            }
        }
        var selectedPattern=ProductProfile.Load().EffectiveLedPatterns["selected"];
        void Status(string state,string? detail=null)=>MidiLearn.Save(folder+"/status.json",new{state,detail,pid=Environment.ProcessId,time=DateTimeOffset.UtcNow,startedAt,expiresAt,mode=seconds.HasValue?"test":"continuous",transport=transport=="usb"?"USB":"Bluetooth",connectionState=state=="active"?"ready":state=="waiting_for_micro"?"connected":state,taskSelection=allowTaskSelection,taskSource,taskSlots=Surface12Map.TaskTargets,commandExecution=commands.Count>0,enabledCommands=commands,analogObservation=observeAnalogs,analogExecution=executeAnalogs,jogSensitivity,leftJogIntervalMs=AnalogSurfaceLogic.LeftIntervalMs,selectedLedMode=selectedPattern.Mode,selectedLedOnMs=selectedPattern.Mode=="blink"?selectedPattern.Durations[0]:(int?)null,selectedLedOffMs=selectedPattern.Mode=="blink"?selectedPattern.Durations[1]:(int?)null,slots=logic.Tasks});
        void CheckTaskSource(string? currentConfig=null)
        {
            string next=Surface12Map.ReadTaskSource(currentConfig??File.ReadAllText(MicroBinding.StatePath));
            if(next==taskSource)return;
            string previous=taskSource;taskSource=next;
            // A pad pressed in the previous source must not select on release in the new one.
            logic.CancelTaskGestures();
            Log(new{kind="task_source_changed",ms=clock.ElapsedMilliseconds,previous,taskSource});
            Status(active?"active":"waiting_for_micro");
        }
        void CheckConfiguration()
        {
            string currentConfig=File.ReadAllText(MicroBinding.StatePath);
            if(Surface12Map.ConfigurationFingerprint(currentConfig,nativeRemapping:!seconds.HasValue)!=config||HashInputs(observeAnalogs)!=mapHash)
                throw new IOException("Mapping/configuration changed");
            CheckTaskSource(currentConfig);
        }
        async Task AnalogEvents(IEnumerable<AnalogIntent> intents,long now)
        {
            foreach(var intent in intents)
            {
                bool sent=false;
                if(executeAnalogs&&intent.Method!="discard")
                {
                    if(intent.Key=="ENC_PRESS"&&intent.Act==1)encoderDown=true;
                    if(intent.Method=="v.oai.rad"&&intent.Distance>0)joystickDisplaced=true;
                    sent=await server.SendAnalogIntent(intent);
                    if(!sent)throw new IOException("Analog HID delivery failed or uncertain; disarmed");
                    if(intent.Key=="ENC_PRESS"&&intent.Act==0)encoderDown=false;
                    if(intent.Method=="v.oai.rad"&&intent.Distance==0)joystickDisplaced=false;
                }
                Log(new{kind="analog_intent",ms=now,intent,execution=sent?"analog_delivered":intent.Method=="discard"?"discarded":"blocked",wire=intent.Method=="discard"?null:intent.Wire()});
            }
        }
        void Leds(bool connected)
        {
            foreach(var binding in map)
            {
                bool on=logic.Led(binding,clock.ElapsedMilliseconds,connected);
                if(last.TryGetValue(binding.Target,out bool old)&&old==on)continue;
                midi.Send(binding.Led(on));last[binding.Target]=on;
                Log(new{kind="led",ms=clock.ElapsedMilliseconds,target=binding.Target,on,source=binding.IsTask?"real_micro_task_state":"local_identification_hold"});
            }
        }
        try
        {
            server.Start();Log(new{kind="session_started",startedAt,expiresAt,continuous=!seconds.HasValue,jogSensitivity,leftJogIntervalMs=AnalogSurfaceLogic.LeftIntervalMs});Status("waiting_for_micro");Console.WriteLine("WAITING for external one-time attach; enabled commands: "+(commands.Count==0?"NONE":string.Join(',',commands)));
            long statusAt=0;
            while(!stop.IsCancellationRequested&&!File.Exists(folder+"/stop"))
            {
                long now=clock.ElapsedMilliseconds;bool connected=server.IsImported;
                if(imported&&!connected)throw new IOException("Micro disconnected");imported|=connected;
                if(overflow!=0||midi.Dropped!=0||midi.InputError)throw new IOException("MIDI or feedback loss");
                if(now-guard>=250){CheckConfiguration();guard=now;}
                while(queue.Reader.TryRead(out var item))
                {
                    logic.Receive(item.Method,item.Value,item.Time);
                    if(item.Method=="v.oai.thstatus") {Log(new{kind="real_task_feedback",ms=now,taskSource,slots=logic.Tasks});if(active)Status("active");}
                }
                now=clock.ElapsedMilliseconds; // Feedback may arrive after the loop's initial clock read.
                bool ready=logic.Ready(now,connected);
                if(active&&!ready)throw new IOException("Micro feedback stale; disarmed");
                if(ready&&!active){active=true;Status("active");Console.WriteLine("ACTIVE: fourteen buttons and LEDs ready; enabled commands: "+(commands.Count==0?"NONE":string.Join(',',commands)));}
                if(now-statusAt>=2000){Status(active?"active":"waiting_for_micro");statusAt=now;}
                while(midi.TryRead(out var packet))
                {
                    long age=(long)(Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency)-packet.ReceivedMs;
                    if(age is <0 or >200)throw new IOException("Stale physical input");
                    if(analog!=null&&ready)await AnalogEvents(analog.Input(packet.Message,clock.ElapsedMilliseconds),clock.ElapsedMilliseconds);
                    if((packet.Message.Status&0xF0) is 0x80 or 0x90 &&
                        map.Any(b=>b.IsTask && b.Signature==$"note:{(packet.Message.Status&15)+1}:{packet.Message.Data1}"))CheckTaskSource();
                    var edge=logic.Input(packet.Message,clock.ElapsedMilliseconds,connected);if(edge==null)continue;
                    bool sent=false;
                    if(allowTaskSelection&&edge.Binding.IsTask&&edge.Act==0&&edge.HeldMs>=20)
                    {
                        int slot=Array.IndexOf(Surface12Map.TaskTargets,edge.Binding.Target);
                        if(logic.Tasks![slot].Status!="off")
                        {
                            // AGxx is the native slot identity, never a cached recent/pinned task ID.
                            // Codex resolves that slot against its selected source; thstatus IDs use
                            // the same positions for lights and empty-slot gating.
                            sent=server.TrySendTaskKey(edge.Binding.WireKey);
                            if(!sent)throw new IOException("Task key not queued; no active HID reader");
                        }
                    }
                    if(edge.Binding.IsDirection && analog != null)
                    {
                        await AnalogEvents(analog.DirectionButton(edge.Binding.Target, edge.Act), clock.ElapsedMilliseconds);
                        sent=executeAnalogs;
                    }
                    if(ShouldSendCommand(edge,commands))
                    {
                        // Track attempted downs too, so uncertain delivery gets a release attempt on cleanup.
                        if(edge.Act==1)emittedHeld.Add(edge.Binding.WireKey);
                        sent=await server.SendCommandEdge(edge.Binding.WireKey,edge.Act);
                        if(!sent)throw new IOException("Command HID delivery failed or uncertain; disarmed");
                        if(edge.Act==0)emittedHeld.Remove(edge.Binding.WireKey);
                    }
                    Log(new{kind="physical_edge",ms=clock.ElapsedMilliseconds,target=edge.Binding.Target,taskSource=edge.Binding.IsTask?taskSource:null,control=edge.Binding.Control,raw=packet.Message.Hex,wire=new{k=edge.Binding.WireKey,act=edge.Act},heldMs=edge.HeldMs,execution=edge.Binding.IsTask?(sent?"task_cycle_queued":"blocked_or_waiting_release"):(sent?"command_edge_delivered":"blocked_command"),provenance=edge.Binding.Provenance});
                }
                if(analog!=null&&ready)await AnalogEvents(analog.Tick(clock.ElapsedMilliseconds),clock.ElapsedMilliseconds);
                Leds(connected);await Task.Delay(10,stop.Token);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e){failure=e.Message;Status("error",e.Message);Log(new{kind="error",detail=e.Message});throw;}
        finally
        {
            analog?.Stop();
            var neutralize=new List<AnalogIntent>();
            if(encoderDown)neutralize.Add(new("encoder.press","v.oai.hid","ENC_PRESS",0));
            if(joystickDisplaced)neutralize.Add(new("joystick.neutral","v.oai.rad",Angle:0,Distance:0));
            foreach(var intent in neutralize)
            {
                bool delivered=false;
                try{delivered=await server.SendAnalogIntent(intent);}catch(Exception e){Console.Error.WriteLine("Analog cleanup failed: "+e.Message);}
                Log(new{kind="analog_cleanup",intent,delivered});
                if(!delivered)failure??="Analog release/neutral unconfirmed after transport loss";
            }
            foreach(string wire in emittedHeld)
            {
                bool delivered=false;
                try {delivered=await server.SendCommandEdge(wire,0);}catch(Exception e){Console.Error.WriteLine("Release cleanup failed: "+e.Message);}
                Log(new{kind="release_cleanup",wireKey=wire,delivered});
                if(!delivered)failure??="Held command release unconfirmed after transport loss";
            }
            logic.Stop();try{Leds(false);}catch(Exception e){failure??="LED cleanup unconfirmed: "+e.Message;Console.Error.WriteLine("LED cleanup unconfirmed: "+e.Message);}
            Status(failure==null?"stopped":"error",failure);Console.CancelKeyPress-=handler;Console.WriteLine("STOPPED: cleanup attempted; detach only recorded port");
        }
        return failure==null?0:1;
        }
    }
    private static async Task<BleGattMidi> ConnectBluetoothAsync(ulong address,string? name)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{return await BleGattMidi.ConnectAsync(address,name,timeout.Token);}
        catch(OperationCanceledException)when(timeout.IsCancellationRequested){throw new TimeoutException("A conexão Bluetooth não respondeu em 15 segundos");}
    }
    private static string HashInputs(bool analog)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProductProfile.Fingerprint()+(analog?JsonSerializer.Serialize(JogSensitivity.Read()):""))));
}
