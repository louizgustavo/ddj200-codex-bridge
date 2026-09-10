using System.Text;
using System.Text.Json;

namespace Ddj200;

public static class BoundedEventLogTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            passed++;
            Console.WriteLine("PASS " + name);
        }
        static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; } catch (T) { return true; }
        }
        static string ReadLive(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string testRoot = Path.GetFullPath("runtime/tests");
        string owned = Path.GetFullPath(Path.Combine(testRoot, "bounded-log-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(owned);
        try
        {
            Check(Throws<ArgumentException>(() => new BoundedEventLog(" ")), "empty folder rejected");
            Check(Throws<ArgumentOutOfRangeException>(() => new BoundedEventLog(owned, 0)), "nonpositive byte limit rejected");
            Check(Throws<ArgumentOutOfRangeException>(() => new BoundedEventLog(owned, 10, 0)), "nonpositive file limit rejected");
            Check(BoundedEventLog.DefaultMaxBytes == 1048576 && BoundedEventLog.DefaultMaxFiles == 5, "default budget is five files of one MiB");
            string historical = Path.Combine(owned, "events-20260909.jsonl");
            string unrelated = Path.Combine(owned, "surface-events.notes.jsonl");
            File.WriteAllText(historical, "historical evidence");
            File.WriteAllText(unrelated, "unrelated data");
            string current = Path.Combine(owned, "surface-events.current.jsonl");
            static string Record(int n) => JsonSerializer.Serialize(new { n });
            // Each single-digit record plus LF is exactly 8 UTF-8 bytes.
            using (var log = new BoundedEventLog(owned, 16, 3))
            {
                for (int n = 0; n < 10; n++) log.WriteLine(Record(n));
                Check(ReadLive(current) == Record(8) + "\n" + Record(9) + "\n", "AutoFlush exposes the current records before disposal");
                Check(Throws<ArgumentException>(() => log.WriteLine(new string('x', 16))), "oversized record rejected including newline byte");
                Check(Throws<ArgumentException>(() => log.WriteLine("{}\n{}")), "embedded newlines rejected");
                Check(ReadLive(current) == Record(8) + "\n" + Record(9) + "\n", "rejected record cannot rotate or modify current log");
            }
            string[] slots = { Path.Combine(owned, "surface-events.2.jsonl"), Path.Combine(owned, "surface-events.1.jsonl"), current };
            Check(slots.All(File.Exists) && slots.All(p => new FileInfo(p).Length <= 16), "rotated slots respect file and byte limits");
            var retained = slots.SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
            try { Check(retained.Select(d => d.RootElement.GetProperty("n").GetInt32()).SequenceEqual(Enumerable.Range(4, 6)), "retained records remain in chronological order across rotation"); }
            finally { foreach (var document in retained) document.Dispose(); }
            Check(Directory.GetFiles(owned).Length == 5, "only three reserved files plus two untouched files exist");
            Check(File.ReadAllText(historical) == "historical evidence" && File.ReadAllText(unrelated) == "unrelated data", "historical and unrelated files survive rotation unchanged");

            string restart = Path.Combine(owned, "restart");
            using (var log = new BoundedEventLog(restart, 64, 3)) log.WriteLine(Record(1));
            using (var log = new BoundedEventLog(restart, 64, 3)) log.WriteLine(Record(2));
            Check(File.ReadAllLines(Path.Combine(restart, "surface-events.current.jsonl")).SequenceEqual(new[] { Record(1), Record(2) }), "restart appends instead of replacing current records");
            using (var log = new BoundedEventLog(owned, 16, 3)) log.WriteLine(Record(0));
            Check(File.ReadAllLines(current).SequenceEqual(new[] { Record(0) }) && File.ReadAllLines(slots[1]).SequenceEqual(new[] { Record(8), Record(9) }), "restart rotates a full current file without losing its records");

            string single = Path.Combine(owned, "single");
            using (var log = new BoundedEventLog(single, 8, 1)) { log.WriteLine(Record(1)); log.WriteLine(Record(2)); }
            Check(Directory.GetFiles(single).Length == 1 && File.ReadAllLines(Path.Combine(single, "surface-events.current.jsonl")).SequenceEqual(new[] { Record(2) }), "one-file budget retains only current generation");

            string unicode = Path.Combine(owned, "unicode");
            const string unicodeRecord = "\"é\"";
            int exactBytes = Encoding.UTF8.GetByteCount(unicodeRecord) + 1;
            using (var log = new BoundedEventLog(unicode, exactBytes, 2)) { log.WriteLine(unicodeRecord); log.WriteLine(unicodeRecord); }
            Check(Directory.GetFiles(unicode).Length == 2 && Directory.GetFiles(unicode).All(p => new FileInfo(p).Length == exactBytes), "UTF-8 byte accounting includes multibyte characters and newline without BOM");

            var disposed = new BoundedEventLog(Path.Combine(owned, "disposed"));
            disposed.Dispose();
            disposed.Dispose();
            Check(Throws<ObjectDisposedException>(() => disposed.WriteLine("{}")), "disposed writer rejects further writes");

            string partial = Path.Combine(owned, "partial");
            Directory.CreateDirectory(partial);
            string partialPath = Path.Combine(partial, "surface-events.current.jsonl");
            File.WriteAllText(partialPath, "{\"partial\":");
            Check(Throws<InvalidDataException>(() => new BoundedEventLog(partial)) && File.ReadAllText(partialPath) == "{\"partial\":", "incomplete prior record is preserved and not joined to a new record");
            Console.WriteLine($"{passed} bounded log checks passed; isolated temporary files only");
            return 0;
        }
        finally
        {
            // The only recursive cleanup is this unique child of the expected tests folder.
            string resolved = Path.GetFullPath(owned);
            if (!resolved.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(resolved) != testRoot || !Path.GetFileName(resolved).StartsWith("bounded-log-", StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the owned test directory");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
