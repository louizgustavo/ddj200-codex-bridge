using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ddj200;

public static class MicroTests
{
    public static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            Console.WriteLine("PASS " + name); passed++;
        }
        var codec = new MicroProtocol();
        var blePacket=BleGattMidi.Encode(new MidiMessage(0x90,0x0B,0x7F));
        Check(blePacket.SequenceEqual(new byte[]{0x80,0x80,0x90,0x0B,0x7F}),"BLE-MIDI output uses validated timestamp-zero frame");
        var bleDecoded=BleGattMidi.Decode(blePacket);
        Check(bleDecoded.Count==1&&bleDecoded[0]==new MidiMessage(0x90,0x0B,0x7F),"BLE-MIDI input normalizes one physical channel message");
        Check(BleGattMidi.Decode(new byte[]{0x00,0x80,0x90,0x0B,0x7F}).Count==0,"BLE-MIDI malformed header fails closed");
        Check(BleGattMidi.TryDecode(new byte[]{0x80,0x80,0x90,0x0B,0x7F,0x81,0x80,0x0B,0x00},out var batched)&&batched.SequenceEqual(new[]{new MidiMessage(0x90,0x0B,0x7F),new MidiMessage(0x80,0x0B,0x00)}),"BLE-MIDI batched messages preserve both physical edges");
        Check(BleGattMidi.TryDecode(new byte[]{0x80,0x80,0xB0,0x21,0x01,0x81,0x21,0x7F},out var running)&&running.SequenceEqual(new[]{new MidiMessage(0xB0,0x21,0x01),new MidiMessage(0xB0,0x21,0x7F)}),"BLE-MIDI running status preserves jog events");
        Check(!BleGattMidi.TryDecode(new byte[]{0x80,0x80,0x90,0x0B},out _),"BLE-MIDI truncated message fails closed");
        Check(!BleGattMidi.TryDecode(new byte[]{0x80,0x80,0x90,0x0B,0x7F,0x81},out _),"BLE-MIDI dangling timestamp fails closed");
        string? observed = null;
        codec.LightingReceived += (method, _) => observed = method;
        var fragments = Request("{\"method\":\"v.oai.rgbcfg\",\"params\":{\"ambient\":{\"label\":\"" + new string('x', 100) + "\"}},\"id\":17}");
        Check(codec.Accept(fragments[0]).Count == 0, "fragment waits for complete JSON without newline");
        IReadOnlyList<byte[]> answer = Array.Empty<byte[]>();
        foreach (var fragment in fragments.Skip(1)) answer = codec.Accept(fragment);
        using (var value = Decode(answer)) Check(value.RootElement.GetProperty("id").GetInt32() == 17 && value.RootElement.GetProperty("result").GetBoolean() && observed == "v.oai.rgbcfg", "fragmented lighting request acknowledged with matching ID");
        answer = codec.Accept(Request("{\"method\":\"device.status\",\"id\":18}")[0]);
        using (var value = Decode(answer)) Check(!value.RootElement.GetProperty("result").TryGetProperty("battery", out _), "status does not invent physical battery");
        answer = codec.Accept(Request("{\"method\":\"unsupported\",\"id\":19}")[0]);
        using (var value = Decode(answer)) Check(value.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32601, "unknown method fails explicitly");
        bool invalid = false;
        try { codec.Accept(new byte[64]); } catch (InvalidDataException) { invalid = true; }
        Check(invalid, "invalid report identity rejected");
        var broken = Request("{\"method\":\"device.status\",\"id\":")[0];
        codec.Accept(broken);
        try { codec.Accept(new byte[64]); } catch (InvalidDataException) { }
        answer = codec.Accept(Request("{\"method\":\"device.status\",\"id\":20}")[0]);
        using (var value = Decode(answer)) Check(value.RootElement.GetProperty("id").GetInt32() == 20, "bad frame clears incomplete RPC before next request");
        invalid = false;
        try
        {
            foreach (var frame in Request("{\"method\":\"" + new string('x', MicroProtocol.MaxJsonBytes))) codec.Accept(frame);
        }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "unfinished JSON accumulation is bounded");
        answer = codec.Accept(Request("{\"method\":\"v.oai.thstatus\",\"params\":false,\"id\":21}")[0]);
        using (var value = Decode(answer)) Check(value.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602, "invalid lighting shape does not acknowledge success");
        Check(MicroDescriptors.Configuration.Length == 41 && MicroDescriptors.Hid[7] == MicroDescriptors.Report.Length && MicroDescriptors.Report.Contains((byte)0x85), "USB descriptor sizes agree");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var server = new MicroUsbIp(0);
        server.Start();
        using (var list = await Connect(server.Port, token))
        {
            byte[] request = Header(0x8005);
            foreach (byte b in request) await list.GetStream().WriteAsync(new[] { b }, token);
            byte[] reply = await Read(list.GetStream(), 328, token);
            Check(MicroUsbIp.U16(reply, 2) == 5 && MicroUsbIp.U32(reply, 8) == 1 && MicroUsbIp.U16(reply, 314) == MicroDescriptors.Product && reply[324] == 3, "fragmented DEVLIST identifies vendor HID");
        }
        using var imported = await Connect(server.Port, token);
        var stream = imported.GetStream();
        await stream.WriteAsync(Import(), token);
        byte[] importReply = await Read(stream, 320, token);
        Check(MicroUsbIp.U32(importReply, 4) == 0 && MicroUsbIp.U32(importReply, 296) == 1, "IMPORT succeeds on loopback");
        using (var duplicate = await Connect(server.Port, token))
        {
            await duplicate.GetStream().WriteAsync(Import(), token);
            Check(MicroUsbIp.U32(await Read(duplicate.GetStream(), 8, token), 4) != 0, "second import rejected");
        }
        await stream.WriteAsync(Submit(1, 1, 0, 18, new byte[] { 0x80, 6, 0, 1, 0, 0, 18, 0 }), token);
        var descriptor = await Return(stream, token);
        Check(descriptor.Status == 0 && descriptor.Payload.SequenceEqual(MicroDescriptors.Device), "control GET_DESCRIPTOR returns device");
        await stream.WriteAsync(Submit(2, 0, 0, 0, new byte[] { 0, 9, 1, 0, 0, 0, 0, 0 }), token);
        Check((await Return(stream, token)).Status == 0, "SET_CONFIGURATION accepted");
        await stream.WriteAsync(Submit(3, 1, 1, 64), token); // Pending interrupt IN.
        byte[] unlink = new byte[48];
        MicroUsbIp.W32(unlink, 0, 2); MicroUsbIp.W32(unlink, 4, 4); MicroUsbIp.W32(unlink, 8, MicroUsbIp.DeviceId); MicroUsbIp.W32(unlink, 20, 3);
        await stream.WriteAsync(unlink, token);
        var unlinked = await Return(stream, token);
        Check(unlinked.Kind == 4 && unlinked.Sequence == 4 && unlinked.Status == -104, "UNLINK cancels pending interrupt without stale completion");
        await stream.WriteAsync(Submit(5, 1, 1, 64), token);
        await stream.WriteAsync(Submit(6, 0, 1, 64).Concat(Request("{\"method\":\"device.status\",\"id\":41}")[0]).ToArray(), token);
        var written = await Return(stream, token, output: true);
        Check(written.Sequence == 6 && written.Status == 0 && written.Actual == 64, "OUT proceeds while IN pending");
        var first = await Return(stream, token);
        var responses = new List<byte[]> { first.Payload };
        uint nextSequence = 7;
        while (!Encoding.UTF8.GetString(Payload(responses)).EndsWith('\n'))
        {
            await stream.WriteAsync(Submit(nextSequence++, 1, 1, 64), token);
            responses.Add((await Return(stream, token)).Payload);
        }
        using (var value = Decode(responses)) Check(first.Sequence == 5 && value.RootElement.GetProperty("id").GetInt32() == 41, "RPC response travels through USB/IP interrupt IN");
        uint physicalRead = nextSequence++;
        await stream.WriteAsync(Submit(physicalRead, 1, 1, 64), token);
        // A real MIDI cycle will use this same queue; this is only a synthetic test.
        bool accepted = false;
        for (int attempt = 0; attempt < 100 && !accepted; attempt++) { accepted = server.TrySendSettingsKey("ACT06"); if (!accepted) await Task.Delay(5, token); }
        Check(accepted, "physical notification queue accepts only with a live HID reader");
        var pressed = await Return(stream, token);
        using (var value = Decode(new[] { pressed.Payload })) Check(pressed.Sequence == physicalRead && value.RootElement.GetProperty("method").GetString() == "v.oai.hid" && value.RootElement.GetProperty("params").GetProperty("act").GetInt32() == 1, "asynchronous notification reaches pending IN without additional OUT");
        await stream.WriteAsync(Submit(nextSequence++, 1, 1, 64), token);
        using (var value = Decode(new[] { (await Return(stream, token)).Payload })) Check(value.RootElement.GetProperty("params").GetProperty("act").GetInt32() == 0, "notification release follows press without duplicate action");
        bool invalidCommand=false;
        try{await server.SendCommandEdge("ACT11",1);}catch(ArgumentException){invalidCommand=true;}
        Check(invalidCommand,"combined microphone rejects inactive ACT11 wire key");
        await stream.WriteAsync(Submit(nextSequence++,1,1,64),token);
        var commandDown=server.SendCommandEdge("ACT10",1);
        using(var value=Decode(new[]{(await Return(stream,token)).Payload}))Check(value.RootElement.GetProperty("params").GetProperty("k").GetString()=="ACT10"&&value.RootElement.GetProperty("params").GetProperty("act").GetInt32()==1,"command press delivered as one real edge");
        Check(await commandDown,"command delivery completion waits for live HID write");
        await stream.WriteAsync(Submit(nextSequence++,1,1,64),token);
        var heldRead=Return(stream,token);
        await Task.Delay(70,token);
        Check(!heldRead.IsCompleted,"microphone hold does not synthesize immediate release");
        var commandUp=server.SendCommandEdge("ACT10",0);
        using(var value=Decode(new[]{(await heldRead).Payload}))Check(value.RootElement.GetProperty("params").GetProperty("act").GetInt32()==0,"microphone release occurs only on supplied physical edge");
        Check(await commandUp,"release delivery separately confirmed");
        foreach(var intent in new[]{new AnalogIntent("ENC_CW","v.oai.hid","ENC_CW",2),new AnalogIntent("encoder.press","v.oai.hid","ENC_PRESS",1),new AnalogIntent("encoder.press","v.oai.hid","ENC_PRESS",0),new AnalogIntent("joystick.up","v.oai.rad",Angle:.75,Distance:1),new AnalogIntent("joystick.neutral","v.oai.rad",Angle:0,Distance:0)})
        {
            await stream.WriteAsync(Submit(nextSequence++,1,1,64),token);
            var delivered=server.SendAnalogIntent(intent);
            using(var value=Decode(new[]{(await Return(stream,token)).Payload}))
            {
                var p=value.RootElement.GetProperty("params");
                Check(value.RootElement.GetProperty("method").GetString()==intent.Method && (intent.Method=="v.oai.hid"?p.GetProperty("k").GetString()==intent.Key&&p.GetProperty("act").GetInt32()==intent.Act:p.GetProperty("a").GetDouble()==intent.Angle&&p.GetProperty("d").GetDouble()==intent.Distance),"native analog wire "+intent.Target+" "+intent.Act);
            }
            Check(await delivered,"analog write completion "+intent.Target);
        }
        bool discardRejected=false;try{await server.SendAnalogIntent(new("deck1.jog","discard",Reason:"jog_touched"));}catch(ArgumentException){discardRejected=true;}
        Check(discardRejected,"touched rotation cannot enter native transport");
        await stream.WriteAsync(Submit(nextSequence++, 1, 2, 64), token);
        Check((await Return(stream, token)).Status == -32, "unsupported endpoint stalls");
        uint abandoned = nextSequence++;
        await stream.WriteAsync(Submit(abandoned, 1, 1, 64), token);
        await stream.WriteAsync(Submit(nextSequence++, 0, 0, 0, new byte[] { 0, 9, 0, 0, 0, 0, 0, 0 }), token);
        var unconfigured = await Return(stream, token);
        var abandonedReply = await Return(stream, token);
        Check(unconfigured.Status == 0 && abandonedReply.Sequence == abandoned && abandonedReply.Status == -108, "unconfigure shuts down pending interrupt");
        Check(!server.TrySendSettingsKey("ACT06"), "unconfigured session refuses physical input queue");
        Check(!await server.SendCommandEdge("ACT10",0),"unconfigured transport refuses held-command release without replay");
        await stream.WriteAsync(Submit(nextSequence++, 1, 1, uint.MaxValue), token);
        var eof = new byte[1];
        Check(await stream.ReadAsync(eof, token) == 0, "oversized transfer disconnects before allocation");
        passed += MicroMidiTests.Run();
        Console.WriteLine($"{passed} Micro checks passed. Synthetic loopback only; no driver, MIDI or Codex action.");
        return 0;
    }
    internal static IReadOnlyList<byte[]> Request(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var frames = new List<byte[]>();
        for (int offset = 0; offset < bytes.Length; offset += 61)
        {
            int count = Math.Min(61, bytes.Length - offset);
            byte[] frame = new byte[64]; frame[0] = 6; frame[1] = 2; frame[2] = (byte)count;
            bytes.AsSpan(offset, count).CopyTo(frame.AsSpan(3)); frames.Add(frame);
        }
        return frames;
    }
    private static byte[] Payload(IEnumerable<byte[]> reports) => reports.SelectMany(r => r.AsSpan(3, r[2]).ToArray()).ToArray();
    private static JsonDocument Decode(IEnumerable<byte[]> reports) => JsonDocument.Parse(Payload(reports));
    private static byte[] Header(ushort operation)
    {
        byte[] bytes = new byte[8]; MicroUsbIp.W16(bytes, 0, 0x0111); MicroUsbIp.W16(bytes, 2, operation); return bytes;
    }
    private static byte[] Import() => Header(0x8003).Concat(Encoding.ASCII.GetBytes(MicroUsbIp.BusId.PadRight(32, '\0'))).ToArray();
    private static byte[] Submit(uint sequence, uint direction, uint endpoint, uint length, byte[]? setup = null)
    {
        byte[] bytes = new byte[48];
        MicroUsbIp.W32(bytes, 0, 1); MicroUsbIp.W32(bytes, 4, sequence); MicroUsbIp.W32(bytes, 8, MicroUsbIp.DeviceId);
        MicroUsbIp.W32(bytes, 12, direction); MicroUsbIp.W32(bytes, 16, endpoint); MicroUsbIp.W32(bytes, 24, length);
        MicroUsbIp.W32(bytes, 32, uint.MaxValue);
        setup?.CopyTo(bytes, 40); return bytes;
    }
    private static async Task<TcpClient> Connect(int port, CancellationToken cancellation)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellation); return client;
    }
    private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken cancellation)
    {
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, cancellation); return bytes;
    }
    private static async Task<(uint Kind, uint Sequence, int Status, int Actual, byte[] Payload)> Return(NetworkStream stream, CancellationToken cancellation, bool output = false)
    {
        byte[] bytes = await Read(stream, 48, cancellation);
        uint kind = MicroUsbIp.U32(bytes, 0);
        int actual = (int)MicroUsbIp.U32(bytes, 24);
        if (actual is < 0 or > 16384) throw new InvalidDataException("Invalid test response length");
        if (MicroUsbIp.U32(bytes, 8) != 0 || MicroUsbIp.U32(bytes, 12) != 0 || MicroUsbIp.U32(bytes, 16) != 0)
            throw new InvalidDataException("USB/IP return reserved fields must be zero");
        return (kind, MicroUsbIp.U32(bytes, 4), unchecked((int)MicroUsbIp.U32(bytes, 20)), actual,
            output || kind == 4 ? Array.Empty<byte>() : await Read(stream, actual, cancellation));
    }
}
