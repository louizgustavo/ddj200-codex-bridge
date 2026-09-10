using System.Text;

namespace Ddj200;

// Owns only current and the configured numbered surface-events slots in this folder.
// Slot 1 is the most recent archive. Existing events-* and unrelated files are untouched.
public sealed class BoundedEventLog : IDisposable
{
    public const int DefaultMaxBytes = 1024 * 1024;
    public const int DefaultMaxFiles = 5;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string folder;
    private readonly long maxBytes;
    private readonly int maxFiles;
    private StreamWriter? writer;
    private bool disposed;

    public BoundedEventLog(string folder, long maxBytes = DefaultMaxBytes, int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        this.folder = Path.GetFullPath(folder);
        this.maxBytes = maxBytes;
        this.maxFiles = maxFiles;
        Directory.CreateDirectory(this.folder);
        // Do not silently delete or truncate an incompatible log from a previous run.
        for (int slot = 0; slot < maxFiles; slot++)
            if (File.Exists(Slot(slot)) && new FileInfo(Slot(slot)).Length > maxBytes)
                throw new InvalidDataException("Existing bounded log exceeds the configured byte limit");
        writer = OpenCurrent();
    }

    private string Slot(int index) => Path.Combine(folder,
        index == 0 ? "surface-events.current.jsonl" : $"surface-events.{index}.jsonl");

    private StreamWriter OpenCurrent()
    {
        // Disallow a second writer while permitting live, read-only observation.
        var stream = new FileStream(Slot(0), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                    throw new InvalidDataException("Current log ends with an incomplete record; preserve it for review");
            }
            stream.Seek(0, SeekOrigin.End);
            return new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
        }
        catch { stream.Dispose(); throw; }
    }

    public void WriteLine(string record)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Length == 0 || record.Contains('\n') || record.Contains('\r'))
            throw new ArgumentException("One nonempty JSONL record without embedded line breaks is required", nameof(record));
        long bytes = (long)Utf8.GetByteCount(record) + 1; // LF, no BOM.
        if (bytes > maxBytes) throw new ArgumentException("Record exceeds the configured log byte limit", nameof(record));
        if (writer == null) throw new IOException("Log is unavailable after a rotation failure");
        if (writer.BaseStream.Length > maxBytes - bytes) Rotate();
        writer!.WriteLine(record);
    }

    private void Rotate()
    {
        writer!.Dispose();
        writer = null;
        // Rotate oldest first; never truncate the current file or touch wildcard matches.
        // If an operation fails, propagate it and stop accepting writes in this instance.
        string oldest = Slot(maxFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int slot = maxFiles - 2; slot >= 0; slot--)
            if (File.Exists(Slot(slot))) File.Move(Slot(slot), Slot(slot + 1));
        writer = OpenCurrent();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        writer?.Dispose();
        writer = null;
    }
}
