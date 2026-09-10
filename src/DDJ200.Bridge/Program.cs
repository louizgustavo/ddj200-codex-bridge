using System.Text.Json;
using Ddj200;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        string command=args.FirstOrDefault()??"help";
        string? Option(string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        if(command=="self-test")
        {
            int result=0;
            result|=Surface12Tests.Run();
            result|=AnalogSurfaceTests.Run();
            result|=BoundedEventLogTests.Run();
            result|=await MicroTests.Run();
            return result;
        }
        if(command=="surface12-self-test")return Surface12Tests.Run();
        if(command=="analog-surface-self-test")return AnalogSurfaceTests.Run();
        if(command=="bounded-log-self-test")return BoundedEventLogTests.Run();
        if(command=="micro-self-test")return await MicroTests.Run();
        if(command=="profile-check")
        {
            var buttons=Surface12Map.Load();
            AnalogSurfaceMap.Validate(allowNativeRemapping:true);
            Console.WriteLine(JsonSerializer.Serialize(new{profile=ProductProfile.ProfilePath,buttons=buttons.Count,analogs=ProductProfile.Load().Analogs.Count,jog=JogSensitivity.Read(),codexConfig=MicroBinding.StatePath}));
            return 0;
        }
        if(command=="ports")
        {
            Console.WriteLine(JsonSerializer.Serialize(new{input=MidiDevice.Ports(false),output=MidiDevice.Ports(true)},MidiLearn.Json));
            return 0;
        }
        if(command=="identify-control")
        {
            int seconds=int.TryParse(Option("--timeout-seconds"),out int parsed)?parsed:20;
            string transport=Option("--transport")??"usb";
            ulong? address=ulong.TryParse(Option("--ble-address"),System.Globalization.NumberStyles.HexNumber,null,out ulong parsedAddress)?parsedAddress:null;
            return await ControlIdentifier.Run(seconds,transport,address,Option("--ble-name"));
        }
        if(command=="surface12-run")
        {
            if(!args.Contains("--continuous")||args.Contains("--seconds"))throw new ArgumentException("Use --continuous without --seconds for installed operation");
            using var engineMutex=new Mutex(true,"Local\\DDJ200.CodexBridge.Engine",out bool created);
            if(!created)throw new InvalidOperationException("Another DDJ-200 Codex Bridge engine is already running");
            string transport=Option("--transport")??"usb";
            ulong? address=ulong.TryParse(Option("--ble-address"),System.Globalization.NumberStyles.HexNumber,null,out ulong parsedAddress)?parsedAddress:null;
            return await Surface12Run.Run(null,args.Contains("--arm"),args.Contains("--allow-task-selection"),Option("--enable-commands"),args.Contains("--observe-analogs"),args.Contains("--execute-analogs"),transport,address,Option("--ble-name"));
        }
        if(command=="help")
        {
            Console.WriteLine("DDJ-200 Codex Bridge 1.0.0\nsurface12-run --continuous --arm --allow-task-selection --enable-commands ACT06,ACT07,ACT08,ACT09,ACT10_ACT11,ACT12 --observe-analogs --execute-analogs\nprofile-check | ports | identify-control --timeout-seconds 20 | self-test");
            return 0;
        }
        throw new ArgumentException("Unknown command; use help");
    }
    catch(Exception e) when(e is ArgumentException or FormatException or OverflowException or InvalidOperationException or IOException or InvalidDataException or JsonException or PlatformNotSupportedException or TimeoutException or OperationCanceledException)
    {
        Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
        return 1;
    }
}
