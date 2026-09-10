using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ddj200;

public record Port(uint Index, string Name, ushort Manufacturer, ushort Product);
public readonly record struct MidiPacket(MidiMessage Message, long ReceivedMs);
public readonly record struct MidiMessage(byte Status, byte Data1, byte Data2)
{
    public uint Packed => (uint)(Status | Data1 << 8 | Data2 << 16);
    public string Hex => $"{Status:X2} {Data1:X2} {Data2:X2}";
    public static MidiMessage Unpack(uint data) => new((byte)data, (byte)(data >> 8), (byte)(data >> 16));
}

// The callback never calls WinMM, writes files, or invokes user actions.
public interface ISurfaceMidi : IDisposable
{
    int Dropped { get; }
    bool InputError { get; }
    bool TryRead(out MidiPacket message);
    void Send(MidiMessage message);
}

public sealed class MidiDevice : ISurfaceMidi
{
    private nint input, output;
    private readonly Native.MidiCallback callback;
    private readonly ConcurrentQueue<MidiPacket> queue = new();
    private int queued, dropped;
    private int inputError;
    private int callbacks, dataCallbacks;
    public int Dropped => Volatile.Read(ref dropped);
    public int CallbackCount => Volatile.Read(ref callbacks);
    public int DataCallbackCount => Volatile.Read(ref dataCallbacks);
    public bool InputError => Volatile.Read(ref inputError) != 0;
    public MidiDevice(Port? inPort, Port? outPort = null)
    {
        callback = OnMidi;
        try
        {
            if (inPort != null) Check(Native.midiInOpen(out input, inPort.Index, callback, 0, 0x30000 | 0x20), "midiInOpen");
            if (outPort != null) Check(Native.midiOutOpen(out output, outPort.Index, 0, 0, 0), "midiOutOpen");
            if (input != 0) Check(Native.midiInStart(input), "midiInStart");
        }
        catch { Dispose(); throw; }
    }
    private void OnMidi(nint handle, uint message, nuint instance, nuint data, nuint timestamp)
    {
        Interlocked.Increment(ref callbacks);
        if (message == 0x3C5 || message == 0x3C6) { Interlocked.Exchange(ref inputError, 1); return; }
        if (message != 0x3C3 && message != 0x3CC) return;
        Interlocked.Increment(ref dataCallbacks);
        if (Interlocked.Increment(ref queued) > 4096)
        {
            Interlocked.Decrement(ref queued);
            Interlocked.Increment(ref dropped);
            return;
        }
        queue.Enqueue(new(MidiMessage.Unpack((uint)data), (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency)));
    }
    public bool TryRead(out MidiPacket message)
    {
        if (!queue.TryDequeue(out message)) return false;
        Interlocked.Decrement(ref queued);
        return true;
    }
    public void Send(MidiMessage message)
    {
        if (output == 0) throw new InvalidOperationException("MIDI output unavailable");
        if (!LedPolicy.IsAllowed(message)) throw new ArgumentException("Output outside documented LED allowlist");
        Check(Native.midiOutShortMsg(output, message.Packed), "midiOutShortMsg");
    }
    public void Dispose()
    {
        if (input != 0) { Native.midiInStop(input); Native.midiInReset(input); Native.midiInClose(input); input = 0; }
        if (output != 0) { Native.midiOutClose(output); output = 0; }
        GC.KeepAlive(callback);
    }
    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation}: MMRESULT={result}");
    }
    public static List<Port> Ports(bool output)
    {
        var ports = new List<Port>();
        uint count = output ? Native.midiOutGetNumDevs() : Native.midiInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            // MIDIOUTCAPSW=84 bytes; MIDIINCAPSW=76. WCHAR device name starts at byte 8.
            int size = output ? 84 : 76;
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = output ? Native.midiOutGetDevCapsW(i, pointer, (uint)size) : Native.midiInGetDevCapsW(i, pointer, (uint)size);
                Check(result, "midiGetDevCaps");
                ports.Add(new(i, Marshal.PtrToStringUni(pointer + 8, 32)!.TrimEnd('\0'),
                    (ushort)Marshal.ReadInt16(pointer), (ushort)Marshal.ReadInt16(pointer + 2)));
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        return ports;
    }
    public static Port Select(IEnumerable<Port> ports, string name)
    {
        var matches = ports.Where(p => p.Name == name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException($"Expected exactly one {name} port; found {matches.Length}");
        return matches[0];
    }
}

internal static class Native
{
    [DllImport("winmm.dll")] public static extern uint midiInMessage(nint deviceId, uint message, nuint data1, nuint data2);
    [DllImport("winmm.dll")] public static extern uint midiOutMessage(nint deviceId, uint message, nuint data1, nuint data2);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate void MidiCallback(nint handle, uint message, nuint instance, nuint data, nuint timestamp);
    [DllImport("winmm.dll")] public static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll")] public static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll")] public static extern uint midiInGetDevCapsW(nuint id, nint caps, uint size);
    [DllImport("winmm.dll")] public static extern uint midiOutGetDevCapsW(nuint id, nint caps, uint size);
    [DllImport("winmm.dll")] public static extern uint midiInOpen(out nint handle, uint id, MidiCallback callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint midiOutOpen(out nint handle, uint id, nint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint midiInStart(nint handle);
    [DllImport("winmm.dll")] public static extern uint midiInStop(nint handle);
    [DllImport("winmm.dll")] public static extern uint midiInReset(nint handle);
    [DllImport("winmm.dll")] public static extern uint midiInClose(nint handle);
    [DllImport("winmm.dll")] public static extern uint midiOutClose(nint handle);
    [DllImport("winmm.dll")] public static extern uint midiOutShortMsg(nint handle, uint message);
}
