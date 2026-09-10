using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Ddj200;

public sealed class BleGattMidi : ISurfaceMidi
{
    public static readonly Guid ServiceUuid=Guid.Parse("03b80e5a-ede8-4b33-a751-6ce34ec4c700");
    public static readonly Guid CharacteristicUuid=Guid.Parse("7772e5db-3868-4112-a1a9-f2669d106bf3");
    private readonly ConcurrentQueue<MidiPacket> queue=new();
    private readonly BluetoothLEDevice device;
    private readonly GattDeviceService service;
    private readonly GattCharacteristic characteristic;
    private int queued,dropped,inputError,disposed;
    public int Dropped=>Volatile.Read(ref dropped);
    public bool InputError=>Volatile.Read(ref inputError)!=0;
    public static byte[] Encode(MidiMessage message)=>[0x80,0x80,message.Status,message.Data1,message.Data2];
    public static IReadOnlyList<MidiMessage> Decode(ReadOnlySpan<byte> bytes)
    {
        return TryDecode(bytes,out var result)?result:[];
    }
    public static bool TryDecode(ReadOnlySpan<byte> bytes,out IReadOnlyList<MidiMessage> messages)
    {
        var result=new List<MidiMessage>();messages=result;
        if(bytes.Length<5||(bytes[0]&0x80)==0)return false;
        int index=1;byte runningStatus=0;
        while(index<bytes.Length)
        {
            if((bytes[index]&0x80)==0)return false; // Every event needs a BLE-MIDI timestamp.
            index++;
            if(index>=bytes.Length)return false;
            byte status;
            if((bytes[index]&0x80)!=0){status=bytes[index++];runningStatus=status;}
            else if(runningStatus!=0)status=runningStatus;
            else return false;
            int kind=status&0xF0;
            if(kind is not (0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0)||index+1>=bytes.Length)return false;
            byte data1=bytes[index++],data2=bytes[index++];
            if(data1>127||data2>127)return false;
            result.Add(new(status,data1,data2));
        }
        return result.Count>0;
    }

    private BleGattMidi(BluetoothLEDevice device,GattDeviceService service,GattCharacteristic characteristic)
    {
        this.device=device;this.service=service;this.characteristic=characteristic;
        characteristic.ValueChanged+=OnValueChanged;device.ConnectionStatusChanged+=OnConnectionChanged;
    }

    public static async Task<BleGattMidi> ConnectAsync(ulong address,string? advertisedName=null,CancellationToken cancellationToken=default)
    {
        var device=await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)??throw new InvalidOperationException("A DDJ-200 não respondeu ao pedido Bluetooth");
        try
        {
            if(!device.Name.StartsWith("DDJ-200",StringComparison.OrdinalIgnoreCase)&&!(advertisedName?.StartsWith("DDJ-200",StringComparison.OrdinalIgnoreCase)??false))throw new InvalidOperationException("O dispositivo escolhido não se identificou como DDJ-200");
            var services=await device.GetGattServicesForUuidAsync(ServiceUuid,BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if(services.Status!=GattCommunicationStatus.Success||services.Services.Count!=1)throw new InvalidOperationException("Serviço Bluetooth MIDI não encontrado");
            var service=services.Services[0];
            var chars=await service.GetCharacteristicsForUuidAsync(CharacteristicUuid,BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if(chars.Status!=GattCommunicationStatus.Success||chars.Characteristics.Count!=1){service.Dispose();throw new InvalidOperationException("Canal Bluetooth MIDI não encontrado");}
            var characteristic=chars.Characteristics[0];
            if(!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)||!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            {service.Dispose();throw new InvalidOperationException("O canal Bluetooth MIDI não oferece entrada e saída compatíveis");}
            var result=new BleGattMidi(device,service,characteristic);
            var notify=await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
            if(notify!=GattCommunicationStatus.Success){result.Dispose();throw new InvalidOperationException("Não foi possível ativar a entrada Bluetooth MIDI");}
            return result;
        }
        catch{device.Dispose();throw;}
    }

    public bool TryRead(out MidiPacket message)
    {
        if(!queue.TryDequeue(out message))return false;
        Interlocked.Decrement(ref queued);return true;
    }

    public void Send(MidiMessage message)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(InputError)throw new IOException("A conexão Bluetooth não está íntegra");
        if(!LedPolicy.IsAllowed(message))throw new ArgumentException("Output outside documented LED allowlist");
        using var writer=new DataWriter();writer.WriteBytes(Encode(message));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var status=characteristic.WriteValueAsync(writer.DetachBuffer(),GattWriteOption.WriteWithoutResponse).AsTask(timeout.Token).GetAwaiter().GetResult();
            if(status!=GattCommunicationStatus.Success)throw new IOException("Falha ao enviar retorno de LED por Bluetooth");
        }
        catch(Exception e)when(e is OperationCanceledException or IOException)
        {
            Interlocked.Exchange(ref inputError,1);
            throw new IOException(e is OperationCanceledException?"O retorno de LED por Bluetooth não respondeu em 3 segundos":e.Message,e);
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender,object args){if(sender.ConnectionStatus!=BluetoothConnectionStatus.Connected)Interlocked.Exchange(ref inputError,1);}
    private void OnValueChanged(GattCharacteristic sender,GattValueChangedEventArgs args)
    {
        try
        {
            using var reader=DataReader.FromBuffer(args.CharacteristicValue);var bytes=new byte[reader.UnconsumedBufferLength];reader.ReadBytes(bytes);
            if(!TryDecode(bytes,out var decoded)){Interlocked.Exchange(ref inputError,1);return;}
            foreach(var message in decoded)
            {
                if(Interlocked.Increment(ref queued)>4096){Interlocked.Decrement(ref queued);Interlocked.Increment(ref dropped);continue;}
                queue.Enqueue(new(message,(long)(Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency)));
            }
        }
        catch{Interlocked.Exchange(ref inputError,1);}
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        characteristic.ValueChanged-=OnValueChanged;device.ConnectionStatusChanged-=OnConnectionChanged;
        try{characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().Wait(1000);}catch{}
        service.Dispose();device.Dispose();
    }
}
