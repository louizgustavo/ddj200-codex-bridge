namespace Ddj200;

// One transport owner per desktop session prevents two copies fighting for LEDs/ports.
public sealed class DeviceOwnership : IDisposable
{
    private readonly Mutex mutex;
    public DeviceOwnership() : this("Local\\DDJ200.CodexBridge") { }
    internal DeviceOwnership(string name)
    {
        mutex = new Mutex(false, name, out bool created);
        if (!created)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Another DDJ200 bridge owns this session; stop it before starting a new capture or LED test");
        }
    }
    // Exclusivity is the lifetime of the named kernel object (createdNew), not a
    // thread-affine acquisition, so async continuations may dispose it safely.
    public void Dispose() => mutex.Dispose();
}
