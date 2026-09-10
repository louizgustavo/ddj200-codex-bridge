using System.Diagnostics;
using System.Text.Json;

namespace Ddj200;

public static class ControlIdentifier
{
    public static async Task<int> Run(int timeoutSeconds,string transport="usb",ulong? bleAddress=null,string? bleName=null)
    {
        if (timeoutSeconds is < 5 or > 60) throw new ArgumentException("--timeout-seconds must be 5..60");
        using var owner=new DeviceOwnership();
        if(transport is not ("usb" or "bluetooth"))throw new ArgumentException("Transport must be usb or bluetooth");
        if(transport=="bluetooth"&&!bleAddress.HasValue)throw new ArgumentException("Bluetooth learning requires the selected DDJ-200");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using ISurfaceMidi midi=transport=="usb"?new MidiDevice(MidiDevice.Select(MidiDevice.Ports(false),"DDJ-200"),null):await BleGattMidi.ConnectAsync(bleAddress!.Value,bleName,timeout.Token);
        var down=new Dictionary<(byte Status,byte Note),(string Control,long At)>();
        var clock=Stopwatch.StartNew();
        while(!timeout.IsCancellationRequested)
        {
            if(midi.Dropped!=0||midi.InputError)throw new IOException("A entrada MIDI perdeu dados; tente novamente");
            while(midi.TryRead(out var packet))
            {
                var message=packet.Message; int type=message.Status&0xF0;
                if(type is not (0x80 or 0x90))continue;
                var allowed=LedPolicy.Targets.SingleOrDefault(x=>(x.Value.Status&0x0F)==(message.Status&0x0F)&&x.Value.Data1==message.Data1);
                if(string.IsNullOrEmpty(allowed.Key))continue;
                var key=((byte)(message.Status&0x0F),message.Data1); bool pressed=type==0x90&&message.Data2>0;
                if(pressed){if(!down.ContainsKey(key))down[key]=(allowed.Key,clock.ElapsedMilliseconds);continue;}
                if(down.Remove(key,out var held)&&clock.ElapsedMilliseconds-held.At>=20)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new{control=held.Control,signature=$"note:{key.Item1+1}:{key.Item2}"}));
                    return 0;
                }
            }
            try{await Task.Delay(10,timeout.Token);}catch(OperationCanceledException){break;}
        }
        Console.Error.WriteLine("Nenhum controle compatível foi pressionado e solto no tempo disponível.");
        return 2;
    }
}
