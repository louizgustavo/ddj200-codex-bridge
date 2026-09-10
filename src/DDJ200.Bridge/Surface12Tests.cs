using System.Text.Json;
namespace Ddj200;
public static class Surface12Tests
{
    public static int Run()
    {
        int passed=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
        bool Led(string state,bool selected,long t)=>Surface12Logic.TaskLed(new(state,selected),t);
        Check(!Led("off",true,0)&&!Led("idle",false,0),"unassigned and idle off");
        Check(Led("working",false,999)&&!Led("working",false,1000)&&!Led("working",false,1499)&&Led("working",false,1500),"working exact 1000/500 boundaries");
        Check(Led("unread",false,999999),"complete unread solid");
        Check(Led("attention",true,249)&&!Led("attention",true,250)&&Led("attention",true,500),"attention overrides selection with 250/250");
        foreach(long t in new long[]{0,249,500,749,1000,1249,2250})Check(Led("error",true,t),"error ON at "+t);
        foreach(long t in new long[]{250,499,750,999,1250,2249})Check(!Led("error",true,t),"error OFF at "+t);
        foreach(string state in new[]{"idle","working","unread"})Check(Led(state,true,0)&&Led(state,true,1999)&&!Led(state,true,2000)&&!Led(state,true,2099)&&Led(state,true,2100)&&Led(state,true,4099)&&!Led(state,true,4100),"selected exact 2000/100 boundaries and precedence "+state);
        Check(!Surface12Logic.TaskLed(new("working",true,false),0),"zero brightness stays dark");
        Check(ProductLedPolicy.IsOn(new("blink",[100,200]),99)&&!ProductLedPolicy.IsOn(new("blink",[100,200]),100)&&ProductLedPolicy.IsOn(new("blink",[100,200]),300),"custom LED timings use exact alternating boundaries");
        bool invalidLed=false;try{ProductLedPolicy.Validate(ProductLedPolicy.Default.ToDictionary(x=>x.Key,x=>x.Key=="attention"?new ProductLedPattern("blink",[25,25]):x.Value));}catch(InvalidDataException){invalidLed=true;}Check(invalidLed,"unsafe custom LED timing rejected before activation");
        var map=Surface12Map.Load();Check(map.Count==12&&map.All(x=>x.Provenance=="product_preset"&&x.CaptureId==null),"load twelve sanitized product preset bindings");
        bool bad=false;try{Surface12Map.Validate(map.Select(x=>x.Target=="ACT06"?x with{Signature=map[0].Signature}:x).ToList());}catch(InvalidDataException){bad=true;}Check(bad,"manual and captured conflict rejected");
        JsonElement Snapshot(int selected=0,int color=16777215)=>JsonSerializer.SerializeToElement(Enumerable.Range(0,6).Select(i=>new{id=i,c=color,b=1.0,e=i==selected?4:1,s=i==selected?0.4:0.0,sk=0,sa=0}).ToArray());
        var logic=new Surface12Logic(map);Check(!logic.Ready(0,true),"connection alone cannot invent task feedback");
        logic.Receive("v.oai.thstatus",Snapshot(),0);Check(logic.Ready(0,true)&&logic.Tasks![0].Selected,"real six-slot schema and selected decode");
        logic.Receive("v.oai.thstatus",Snapshot(),900);Check(!logic.Led(map[0],2000,true),"unchanged snapshots do not restart blinking phase");
        Check(!logic.Ready(75901,true),"stale feedback is not ready");
        logic.Receive("device.status",JsonSerializer.SerializeToElement(new{}),75000);Check(logic.Ready(76000,true),"actual device request refreshes unchanged transport health");
        Check(!logic.Ready(76000,false)&&!logic.Led(map[6],76000,false),"disconnect extinguishes local and task LEDs");
        bad=false;try{Surface12Logic.ParseTasks(Snapshot(0,123));}catch(InvalidDataException){bad=true;}Check(bad,"unknown color fails closed");
        bad=false;try{Surface12Logic.ParseTasks(JsonSerializer.SerializeToElement(new[]{new{id=0}}));}catch(InvalidDataException){bad=true;}Check(bad,"partial task snapshot rejected");
        var cmd=map.Single(x=>x.Target=="ACT10_ACT11");var on=cmd.Led(true);var off=cmd.Led(false);
        Check(cmd.WireKey=="ACT10","combined microphone uses actual ACT10 wire key");
        var enabled=Surface12Run.EnabledCommands(null);
        Check(map.Where(x=>!x.IsTask).All(x=>!Surface12Run.ShouldSendCommand(new(x,1,0),enabled)),"all consequential commands blocked by default");
        enabled=Surface12Run.EnabledCommands("ACT06,ACT10_ACT11");
        Check(Surface12Run.ShouldSendCommand(new(cmd,1,0),enabled)&&Surface12Run.ShouldSendCommand(new(cmd,0,1500),enabled),"explicit microphone permission permits both physical edges");
        Check(!Surface12Run.ShouldSendCommand(new(map.Single(x=>x.Target=="ACT07"),1,0),enabled)&&!Surface12Run.ShouldSendCommand(new(map[0],1,0),enabled),"enabling FAST and MIC cannot approve or select a task");
        bad=false;try{Surface12Run.EnabledCommands("ACT11");}catch(ArgumentException){bad=true;}Check(bad,"inactive second microphone switch cannot be enabled");
        Check(logic.Input(on,76000,true)?.Act==1,"physical microphone down translates once");
        Check(logic.Input(on,76010,true)==null,"duplicate down does not repeat command");
        Check(logic.Led(cmd,76999,true)&&!logic.Led(cmd,77000,true)&&logic.Led(cmd,78000,true),"held command 1000 ON 1000 OFF");
        var release=logic.Input(off,77500,true);Check(release?.Act==0&&release.HeldMs==1500&&logic.Led(cmd,77500,true),"release preserved and LED immediately solid");
        Check(logic.Input(off,77501,true)==null,"orphan duplicate release ignored");
        Check(logic.Input(new(0xB0,0x22,65),78000,true)==null,"analogs outside scope ignored");
        logic.Stop();Check(!logic.Led(cmd,78000,true)&&logic.Input(on,78000,true)==null,"stopped state blocks input and clears LEDs");
        Check(Surface12Map.ConfigurationFingerprint(nativeRemapping:true).Length==64,"current native six-key structure guarded");
        var reviewedAt=DateTimeOffset.UtcNow;
        var review=JsonSerializer.SerializeToElement(new{source="live_Creator_Micro_accessibility_and_visual_order",agentSource="recent",reviewedAt,slots=Enumerable.Range(0,6).Select(i=>new{id=i,target=Surface12Map.TaskTargets[i],title="Synthetic task "+i}).ToArray()});
        Surface12Map.ValidateTaskReview(review,reviewedAt);Check(true,"selection validates six identified current positions");
        bad=false;try{Surface12Map.ValidateTaskReview(review,reviewedAt.AddMinutes(11));}catch(InvalidDataException){bad=true;}Check(bad,"stale identity inventory cannot silently enable selection");
        Check(Surface12Run.SessionTimeout(null,true)==Timeout.InfiniteTimeSpan,"continuous session has no expiration");
        Check(Surface12Run.SessionTimeout(600,true)==TimeSpan.FromMinutes(10),"test window remains bounded");
        bad=false;try{Surface12Run.SessionTimeout(null,false);}catch(ArgumentException){bad=true;}Check(bad,"continuous mode still requires explicit arming");
        bad=false;try{Surface12Run.SessionTimeout(601,true);}catch(ArgumentException){bad=true;}Check(bad,"oversized test window remains rejected");
        const string micro="[desktop]\ncodex-micro-agent-source = \"recent\"\n[desktop.codex-micro-layout]\nversion = 1\n";
        Surface12Map.ValidateRecentTaskSource(micro);Check(true,"continuous task selection accepts explicit recent policy");
        bad=false;try{Surface12Map.ValidateRecentTaskSource(micro.Replace("recent","pinned"));}catch(InvalidDataException){bad=true;}Check(bad,"changed task-source policy cannot silently enable continuous selection");
        bad=false;try{Surface12Map.ValidateRecentTaskSource("[desktop]\nfontSize = 13\n");}catch(InvalidDataException){bad=true;}Check(bad,"missing explicit task source rejected");
        Check(Surface12Map.MicroConfigurationFingerprint(micro)==Surface12Map.MicroConfigurationFingerprint("model=\"different\"\n"+micro+"[unrelated]\nvalue=1\n"),"unrelated Codex settings cannot terminate Micro runtime");
        Check(Surface12Map.MicroConfigurationFingerprint(micro)!=Surface12Map.MicroConfigurationFingerprint(micro.Replace("recent","pinned")),"Micro source changes remain guarded");
        Check(Surface12Map.MicroConfigurationFingerprint(micro)!=Surface12Map.MicroConfigurationFingerprint(micro.Replace("version = 1","version = 2")),"Micro layout changes remain guarded");
        string nativeConfig=File.ReadAllText(MicroBinding.StatePath);
        string nativeHash=Surface12Map.ConfigurationFingerprint(nativeConfig,true);
        Check(nativeHash==Surface12Map.ConfigurationFingerprint(nativeConfig.Replace("toggleThreadPin","navigateBack"),true),"native command reassignment preserves physical-key compatibility");
        bad=false;try{Surface12Map.ConfigurationFingerprint(nativeConfig.Replace("separateMicrophoneKeys = false","separateMicrophoneKeys = true"),true);}catch(InvalidDataException){bad=true;}Check(bad,"incompatible split microphone layout still rejected");
        bad=false;try{Surface12Map.ConfigurationFingerprint(nativeConfig.Replace("commandId = \"toggleThreadPin\"","commandId = 42"),true);}catch(InvalidDataException){bad=true;}Check(bad,"malformed native command assignment rejected");
        Console.WriteLine($"{passed} surface12 checks passed; hardware unopened; no Codex actions");return 0;
    }
}
