using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Ddj200;

// USB/IP v1.1.1, original implementation based on the Linux protocol specification.
// Listening alone does not install a driver, attach a device, or open MIDI ports.
public sealed class MicroUsbIp : IAsyncDisposable
{
    public const uint DeviceId = 0x00010001;
    public const string BusId = "1-1";
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> clients = new();
    private int imported;
    private Task? acceptLoop;
    private Session? activeSession;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public event Action<string, System.Text.Json.JsonElement>? LightingReceived;
    public bool IsImported => Volatile.Read(ref activeSession) != null;
    public bool TrySendTaskKey(string slot)
    {
        if (!Surface12Map.TaskTargets.Contains(slot)) throw new ArgumentException("Only six task keys allowed");
        return Volatile.Read(ref activeSession)?.TrySendKey(slot) == true;
    }
    public static bool IsCommandWireKey(string key) => key is "ACT06" or "ACT07" or "ACT08" or "ACT09" or "ACT10" or "ACT12";
    // Completion means written to a live HID reader, never application execution acknowledgment.
    public async Task<bool> SendCommandEdge(string key, int act)
    {
        if (!IsCommandWireKey(key) || act is not (0 or 1)) throw new ArgumentException("Invalid command edge");
        return await SendConfirmed((session,completion)=>session.TrySendEdge(key,act,completion));
    }
    public async Task<bool> SendAnalogIntent(AnalogIntent intent)
    {
        bool valid=intent.Method=="v.oai.hid"
            ? (intent.Key is "ENC_CW" or "ENC_CC" && intent.Act==2) || (intent.Key=="ENC_PRESS" && intent.Act is 0 or 1)
            : intent.Method=="v.oai.rad" && intent.Target.StartsWith("joystick.") && intent.Distance is 0 or 1 && intent.Angle is 0 or 0.25 or 0.5 or 0.75;
        if(!valid)throw new ArgumentException("Invalid or discarded analog intent");
        return await SendConfirmed((session,completion)=>session.TrySendAnalog(intent,completion));
    }
    private async Task<bool> SendConfirmed(Func<Session,TaskCompletionSource<bool>,bool> enqueue)
    {
        var session = Volatile.Read(ref activeSession);
        if (session == null) return false;
        long deadline = Environment.TickCount64 + 250;
        while (Environment.TickCount64 < deadline && ReferenceEquals(session, Volatile.Read(ref activeSession)))
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (enqueue(session, completion))
            {
                try { return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(300)); }
                catch (TimeoutException) { return false; } // Never retry an uncertain write.
            }
            await Task.Delay(5);
        }
        return false;
    }
    public bool TrySendSettingsKey(string slot)
    {
        if (!MicroBinding.IsSlot(slot)) throw new ArgumentException("Invalid action slot");
        return Volatile.Read(ref activeSession)?.TrySendKey(slot) == true;
    }

    public MicroUsbIp(int port = 3240)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        listener = new(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
    }
    public void Start()
    {
        if (acceptLoop != null) throw new InvalidOperationException("Already started");
        listener.Start(8);
        acceptLoop = AcceptLoop();
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                clients.RemoveAll(t => t.IsCompleted);
                if (clients.Count >= 8) { client.Dispose(); continue; }
                clients.Add(Serve(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        bool ownsImport = false;
        using (client)
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                byte[] header = new byte[8];
                await stream.ReadExactlyAsync(header, handshake.Token);
                if (U16(header, 0) != 0x0111 || U32(header, 4) != 0) return;
                int operation = U16(header, 2);
                if (operation == 0x8005)
                {
                    byte[] reply = new byte[12 + 312 + 4];
                    W16(reply, 0, 0x0111); W16(reply, 2, 5); W32(reply, 8, 1);
                    DeviceRecord().CopyTo(reply, 12);
                    reply[324] = 3;
                    await stream.WriteAsync(reply, handshake.Token);
                }
                else if (operation == 0x8003)
                {
                    byte[] bus = new byte[32];
                    await stream.ReadExactlyAsync(bus, handshake.Token);
                    bool validBus = bus.SequenceEqual(FixedString(BusId, 32));
                    ownsImport = validBus && Interlocked.CompareExchange(ref imported, 1, 0) == 0;
                    byte[] reply = new byte[ownsImport ? 320 : 8];
                    W16(reply, 0, 0x0111); W16(reply, 2, 3); W32(reply, 4, ownsImport ? 0u : 1u);
                    if (ownsImport) DeviceRecord().CopyTo(reply, 8);
                    await stream.WriteAsync(reply, handshake.Token);
                    if (ownsImport)
                    {
                        var protocol = new MicroProtocol();
                        protocol.LightingReceived += (method, parameters) => LightingReceived?.Invoke(method, parameters);
                        var session = new Session(stream, protocol);
                        Volatile.Write(ref activeSession, session);
                        try { await session.Run(stop.Token); }
                        finally { Volatile.Write(ref activeSession, null); session.Close(); }
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException)
            {
                // Malformed/disconnected clients terminate only their own session.
            }
            finally { if (ownsImport) Interlocked.Exchange(ref imported, 0); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        if (acceptLoop != null) await acceptLoop;
        await Task.WhenAll(clients);
        stop.Dispose();
    }
    internal static byte[] DeviceRecord()
    {
        byte[] record = new byte[312];
        FixedString("/ddj-micro-experimental/1-1", 256).CopyTo(record, 0);
        FixedString(BusId, 32).CopyTo(record, 256);
        W32(record, 288, 1); W32(record, 292, 1); W32(record, 296, 2); // Full speed
        W16(record, 300, MicroDescriptors.Vendor); W16(record, 302, MicroDescriptors.Product);
        W16(record, 304, 0x0100); record[309] = 1; record[310] = 1; record[311] = 1;
        return record;
    }
    private static byte[] FixedString(string value, int length)
    {
        var bytes = new byte[length]; Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0); return bytes;
    }
    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
    internal static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
    internal static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    internal static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);

    private sealed class Session(NetworkStream stream, MicroProtocol protocol)
    {
        private sealed record KeyRequest(string Slot, long Expires, int? Act = null, TaskCompletionSource<bool>? Completion = null, AnalogIntent? Analog=null);
        private readonly Channel<KeyRequest> keys = Channel.CreateBounded<KeyRequest>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Dictionary<uint, int> pending = new();
        private readonly Queue<byte[]> reports = new();
        private byte configuration, idle;
        private int configured, readers;
        public bool TrySendKey(string slot) => Volatile.Read(ref configured) == 1 && Volatile.Read(ref readers) > 0 && keys.Writer.TryWrite(new(slot, Environment.TickCount64 + 250));
        public bool TrySendEdge(string slot, int act, TaskCompletionSource<bool> completion) => Volatile.Read(ref configured) == 1 && Volatile.Read(ref readers) > 0 && keys.Writer.TryWrite(new(slot, Environment.TickCount64 + 250, act, completion));
        public bool TrySendAnalog(AnalogIntent intent,TaskCompletionSource<bool> completion)=>Volatile.Read(ref configured)==1 && Volatile.Read(ref readers)>0 && keys.Writer.TryWrite(new("",Environment.TickCount64+250,null,completion,intent));
        public void Close() { Volatile.Write(ref configured, 0); keys.Writer.TryComplete(); while (keys.Reader.TryRead(out var key)) key.Completion?.TrySetResult(false); }

        private async Task<(byte[] Header, byte[] Output)> ReadCommand(CancellationToken cancellation)
        {
            byte[] header = new byte[48];
            await stream.ReadExactlyAsync(header, cancellation);
            uint kind = U32(header, 0), length = U32(header, 24), direction = U32(header, 12);
            if (kind == 1 && (length > 16384 || U32(header, 32) is not (0 or uint.MaxValue)))
                throw new InvalidDataException("Transfer limit or unsupported isochronous transfer");
            byte[] output = kind == 1 && direction == 0 ? new byte[(int)length] : Array.Empty<byte>();
            if (output.Length != 0) await stream.ReadExactlyAsync(output, cancellation);
            return (header, output);
        }

        public async Task Run(CancellationToken cancellation)
        {
            Task<(byte[] Header, byte[] Output)> read = ReadCommand(cancellation);
            Task<bool> keyReady = keys.Reader.WaitToReadAsync(cancellation).AsTask();
            while (true)
            {
                await Task.WhenAny(read, keyReady);
                if (keyReady.IsCompleted)
                {
                    if (!await keyReady) return;
                    while (keys.Reader.TryRead(out var key))
                    {
                        // Never replay input after congestion, disconnect or unconfigure.
                        if (configuration == 1 && pending.Count > 0 && reports.Count == 0 && Environment.TickCount64 <= key.Expires)
                        {
                            if(key.Analog is { } analog)Enqueue(MicroProtocol.Encode(analog.Wire()));
                            else if (key.Act is { } act) Enqueue(MicroProtocol.Encode(new { method = "v.oai.hid", @params = new { k = key.Slot, act } }));
                            else
                            {
                                Enqueue(MicroProtocol.Encode(new { method = "v.oai.hid", @params = new { k = key.Slot, act = 1 } }));
                                Enqueue(MicroProtocol.Encode(new { method = "v.oai.hid", @params = new { k = key.Slot, act = 0 } }));
                            }
                            await Drain(cancellation);
                            key.Completion?.TrySetResult(true);
                        }
                        else key.Completion?.TrySetResult(false);
                    }
                    keyReady = keys.Reader.WaitToReadAsync(cancellation).AsTask();
                }
                if (!read.IsCompleted) continue;
                var packet = await read;
                byte[] command = packet.Header, output = packet.Output;
                uint kind = U32(command, 0), sequence = U32(command, 4);
                uint direction = U32(command, 12), endpoint = U32(command, 16);
                if (U32(command, 8) != DeviceId || direction > 1 || endpoint > 15 || pending.ContainsKey(sequence))
                    throw new InvalidDataException("Invalid USB/IP header");
                if (kind == 2)
                {
                    int status = pending.Remove(U32(command, 20)) ? -104 : 0;
                    await Reply(4, sequence, status, Array.Empty<byte>(), 0, cancellation);
                    Volatile.Write(ref readers, pending.Count);
                    read = ReadCommand(cancellation);
                    continue;
                }
                if (kind != 1) throw new InvalidDataException("Unsupported USB/IP command");
                uint length = U32(command, 24), isoCount = U32(command, 32);
                if (length > 16384 || isoCount is not (0 or uint.MaxValue))
                    throw new InvalidDataException("Transfer limit or unsupported isochronous transfer");
                int statusCode = 0, actual = 0;
                byte[] data = Array.Empty<byte>();
                if (endpoint == 0)
                {
                    var setup = command.AsSpan(40).ToArray();
                    (statusCode, data) = Control(setup, direction, output);
                    int setupLength = setup[6] | setup[7] << 8;
                    data = data.Take(Math.Min((int)length, setupLength)).ToArray();
                    actual = direction == 1 ? data.Length : statusCode == 0 ? output.Length : 0;
                }
                else if (endpoint == 1 && configuration == 1 && length == 64)
                {
                    if (direction == 1)
                    {
                        if (pending.Count >= 32) throw new InvalidDataException("Pending URB limit");
                        pending.Add(sequence, (int)length);
                        Volatile.Write(ref readers, pending.Count);
                        await Drain(cancellation);
                        read = ReadCommand(cancellation);
                        continue;
                    }
                    try { Enqueue(protocol.Accept(output)); actual = output.Length; }
                    catch (InvalidDataException) { statusCode = -32; }
                }
                else statusCode = -32;
                await Reply(3, sequence, statusCode, direction == 1 ? data : Array.Empty<byte>(), actual, cancellation);
                if (configuration == 0)
                {
                    foreach (uint queuedSequence in pending.Keys.ToArray())
                        await Reply(3, queuedSequence, -108, Array.Empty<byte>(), 0, cancellation);
                    pending.Clear();
                    Volatile.Write(ref readers, 0);
                }
                await Drain(cancellation);
                read = ReadCommand(cancellation);
            }
        }
        private (int Status, byte[] Data) Control(byte[] setup, uint direction, byte[] output)
        {
            int requestType = setup[0], request = setup[1], value = setup[2] | setup[3] << 8;
            int index = setup[4] | setup[5] << 8, length = setup[6] | setup[7] << 8;
            if (((requestType >> 7) & 1) != direction || (direction == 0 && length != output.Length)) return Stall();
            if (request == 6 && requestType is 0x80 or 0x81)
            {
                int type = value >> 8;
                bool validRecipient = type is 0x21 or 0x22 ? requestType == 0x81 && index == 0 : requestType == 0x80;
                var descriptor = validRecipient ? MicroDescriptors.Get(type, value & 255) : null;
                return descriptor == null ? Stall() : (0, descriptor);
            }
            if (requestType == 0 && request == 9 && value is 0 or 1 && index == 0 && length == 0)
            {
                configuration = (byte)value;
                Volatile.Write(ref configured, configuration);
                while (keys.Reader.TryRead(out var key)) key.Completion?.TrySetResult(false);
                if (configuration == 0) reports.Clear();
                return Ok();
            }
            if (requestType == 0x80 && request == 8 && value == 0 && index == 0 && length == 1) return (0, new[] { configuration });
            bool statusRecipient = requestType switch { 0x80 => index == 0, 0x81 => index == 0, 0x82 => index is 0 or 1 or 0x81, _ => false };
            if (statusRecipient && request == 0 && value == 0 && length == 2)
                return (0, new byte[2]);
            if (requestType == 0x81 && request == 10 && index == 0 && value == 0 && length == 1 && configuration == 1)
                return (0, new byte[1]);
            if (requestType == 1 && request == 11 && index == 0 && value == 0 && length == 0 && configuration == 1) return Ok();
            if (requestType == 0x21 && request == 10 && index == 0 && (value & 255) is 0 or 6 && length == 0)
            { idle = (byte)(value >> 8); return Ok(); }
            if (requestType == 0xA1 && request == 2 && index == 0 && value is 0 or 6 && length == 1) return (0, new[] { idle });
            if (requestType == 0x21 && request == 9 && value == 0x0206 && index == 0 && configuration == 1)
            {
                try { Enqueue(protocol.Accept(output)); return Ok(); }
                catch (InvalidDataException) { return Stall(); }
            }
            return Stall();
        }
        private static (int, byte[]) Ok() => (0, Array.Empty<byte>());
        private static (int, byte[]) Stall() => (-32, Array.Empty<byte>());
        private void Enqueue(IReadOnlyList<byte[]> values)
        {
            if (reports.Count + values.Count > 256) throw new InvalidDataException("Report queue limit");
            foreach (var value in values) reports.Enqueue(value);
        }
        private async Task Drain(CancellationToken cancellation)
        {
            while (pending.Count > 0 && reports.Count > 0)
            {
                uint sequence = pending.First().Key;
                pending.Remove(sequence);
                Volatile.Write(ref readers, pending.Count);
                byte[] data = reports.Dequeue();
                await Reply(3, sequence, 0, data, data.Length, cancellation);
            }
        }
        private async Task Reply(uint kind, uint sequence, int status, byte[] data, int actual, CancellationToken cancellation)
        {
            byte[] reply = new byte[48 + data.Length];
            W32(reply, 0, kind); W32(reply, 4, sequence); W32(reply, 20, unchecked((uint)status));
            if (kind == 3) { W32(reply, 24, (uint)actual); W32(reply, 32, uint.MaxValue); }
            data.CopyTo(reply, 48);
            await stream.WriteAsync(reply, cancellation);
        }
    }
}
