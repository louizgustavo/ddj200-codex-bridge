namespace Ddj200;

public static class SharedFile
{
    private static long contentionRetries;
    public static long ContentionRetries => Interlocked.Read(ref contentionRetries);
    // Atomic publishers replace the directory entry. Readers must allow deletion
    // of that entry while retaining their own handle to the complete old snapshot.
    // Do not share Write: a read must never consume an in-place partial config.
    public static FileStream OpenSnapshot(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public static string ReadSnapshot(string path)
    {
        using var stream = OpenSnapshot(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string ReadConfiguration(string path) =>
        OnSharingViolation(() => ReadSnapshot(path), Thread.Sleep);

    public static void ReplaceSnapshot(string replacement,string destination) =>
        OnSharingViolation(()=>{File.Replace(replacement,destination,null);return true;},Thread.Sleep,replacing:true);

    // Only Windows sharing/byte-lock conflicts warrant another read. The 50 ms
    // maximum wait stays below the 200 ms MIDI-age limit; persistent locks,
    // permissions, missing files and invalid content still fail closed.
    internal static T OnSharingViolation<T>(Func<T> read, Action<int> wait,bool replacing=false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return read(); }
            // ReplaceFile error 1175 preserves BOTH names and can safely be tried
            // again. Never retry 1176/1177, which can leave a partially moved file.
            // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew
            catch (IOException e) when (attempt < 5 && ((e.HResult & 0xffff) is 32 or 33 || replacing && (e.HResult & 0xffff)==1175))
            {
                Interlocked.Increment(ref contentionRetries);
                wait(10);
            }
        }
    }
}
