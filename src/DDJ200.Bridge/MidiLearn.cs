using System.Diagnostics;
using System.Text.Json;

namespace Ddj200;

public record LearnSample(long Milliseconds, string Raw, int Channel, string Type, int Number, int Value, string? KnownControl, string Edge);
public record LearnCandidate(string Id, string Target, string Kind, DateTimeOffset CapturedAt, List<LearnSample> Samples, List<string> Signatures, List<string> Issues);
public sealed class LearnDraft
{
    public int Version { get; set; } = 1;
    public int Revision { get; set; }
    public string State { get; set; } = "idle";
    public string? Target { get; set; }
    public string? CaptureId { get; set; }
    public List<LearnCandidate> Candidates { get; set; } = new();
    public Dictionary<string, LearnCandidate> Associations { get; set; } = new();
}

public static class LearnPolicy
{
    public static bool TargetExists(string target) =>
        new[] { "AG00", "AG01", "AG02", "AG03", "AG04", "AG05", "ACT06", "ACT07", "ACT08", "ACT09", "ACT10_ACT11", "ACT12", "ENC_CW", "ENC_CC", "encoder.click", "encoder.longPress", "joystick.up", "joystick.right", "joystick.down", "joystick.left" }.Contains(target);
    public static LearnSample Sample(MidiMessage m, long now)
    {
        int type = m.Status & 0xF0;
        string kind = type is 0x80 or 0x90 ? "note" : type == 0xB0 ? "cc" : "other";
        string edge = kind == "note" ? type == 0x80 || m.Data2 == 0 ? "up" : "down" : "value";
        string? control = new Decoder().Decode(m, now)?.Control;
        return new(now, m.Hex, (m.Status & 15) + 1, kind, m.Data1, m.Data2, control, edge);
    }
    public static LearnCandidate Analyze(string id, string target, string kind, List<LearnSample> samples, IReadOnlyDictionary<string, LearnCandidate> existing)
    {
        var issues = new List<string>();
        var signatures = new List<string>();
        var noteGroups = samples.Where(s => s.Type == "note").GroupBy(s => (s.Channel, s.Number)).ToList();
        var ccGroups = samples.Where(s => s.Type == "cc").GroupBy(s => (s.Channel, s.Number)).ToList();
        if (samples.Count == 0) issues.Add("No gesture");
        if (samples.Any(s => s.Type == "other" || s.Number > 127 || s.Value > 127)) issues.Add("Unsupported MIDI message");
        foreach (var notes in noteGroups)
        {
            bool down = false; int cycles = 0; long pressed = 0;
            foreach (var s in notes)
            {
                if (s.Edge == "down") { if (!down) { down = true; pressed = s.Milliseconds; } }
                else if (!down) { issues.Add("Orphan or duplicate release"); }
                else { down = false; cycles++; if (s.Milliseconds - pressed < 20) issues.Add("Press too short to distinguish bounce"); }
            }
            if (down || cycles != 1) issues.Add("Expected one complete press/release per note");
        }
        if (kind == "button")
        {
            if (noteGroups.Count != 1 || ccGroups.Count != 0) issues.Add("Multiple controls or motion during button capture; repeat or choose gesture type");
            if (noteGroups.Count == 1) signatures.Add($"note:{noteGroups[0].Key.Channel}:{noteGroups[0].Key.Number}");
        }
        else if (kind == "relative")
        {
            if (ccGroups.Count != 1) issues.Add("Expected one relative movement stream");
            if (noteGroups.Any(g => g.Any(s => s.KnownControl?.Contains("jog.touch") != true))) issues.Add("Non-touch button mixed with rotation");
            if (ccGroups.Count == 1)
            {
                var group = ccGroups[0];
                if (group.Any(s => s.KnownControl?.Contains(".jog.") != true)) issues.Add("Uncalibrated CC encoding; direction cannot be guessed");
                var directions = group.Select(s => Math.Sign(s.Value - 64)).Where(x => x != 0).Distinct().ToArray();
                if (directions.Length != 1) issues.Add("Move in only one direction per stage");
                else signatures.Add($"cc:{group.Key.Channel}:{group.Key.Number}:{(directions[0] > 0 ? "positive" : "negative")}");
            }
        }
        else issues.Add("Unknown gesture type");
        foreach (var pair in existing)
            if (pair.Key == target) issues.Add("Target already associated; explicit revision required");
            else if (pair.Value.Signatures.Intersect(signatures).Any()) issues.Add($"Physical control already assigned to {pair.Key}");
        return new(id, target, kind, DateTimeOffset.UtcNow, samples.ToList(), signatures, issues.Distinct().ToList());
    }
}

public static class MidiLearn
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static LearnDraft Read(string path)
    {
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
                !document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
                !document.RootElement.TryGetProperty("associations", out var associations) || associations.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Existing file is not a MIDI learn draft; it will not be overwritten");
        }
        var draft = File.Exists(path) ? JsonSerializer.Deserialize<LearnDraft>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid learn draft") : new LearnDraft();
        if (draft.Version != 1) throw new InvalidDataException("Unsupported learn draft version");
        return draft;
    }
    public static void Save(string path, object value)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, full, true);
    }
    public static int Accept(string path, string candidateId)
    {
        using var owner = new DeviceOwnership(); // Cannot edit a draft during active capture.
        var draft = Read(path);
        var candidate = draft.Candidates.SingleOrDefault(c => c.Id == candidateId) ?? throw new InvalidDataException("Unknown candidate");
        var checkedCandidate = LearnPolicy.Analyze(candidate.Id, candidate.Target, candidate.Kind, candidate.Samples, draft.Associations);
        if (checkedCandidate.Issues.Count != 0) throw new InvalidDataException(string.Join("; ", checkedCandidate.Issues));
        draft.Associations.Add(candidate.Target, candidate);
        draft.State = "idle"; draft.Revision++; Save(path, draft);
        Console.WriteLine($"Draft saved: {candidate.Target}; no definitive map or Codex configuration was changed");
        return 0;
    }
    public static async Task<int> Capture(string path, string target, string kind, bool arm)
    {
        if (!arm || !LearnPolicy.TargetExists(target) || kind is not ("button" or "relative")) throw new ArgumentException("Explicit --arm, catalog --target and --kind button|relative required");
        using var owner = new DeviceOwnership();
        var draft = Read(path);
        if (draft.Associations.ContainsKey(target)) throw new InvalidOperationException("Target already learned; draft must be reviewed before replacement");
        path = Path.GetFullPath(path);
        string id = Guid.NewGuid().ToString("N"), status = path + ".status.json", cancel = path + ".cancel";
        if (File.Exists(cancel)) throw new InvalidOperationException("Existing cancellation marker; explicitly remove it before rearming");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var raw = new StreamWriter(Path.Combine(Path.GetDirectoryName(path)!, "capture-" + id + ".jsonl")) { AutoFlush = true };
        var input = MidiDevice.Select(MidiDevice.Ports(false), "DDJ-200");
        using var midi = new MidiDevice(input, null); // Input only: never opens a MIDI output port or USB/IP server.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        var clock = Stopwatch.StartNew();
        var samples = new List<LearnSample>();
        var held = new HashSet<(int, int)>();
        long first = -1, last = -1;
        void Status(string state, object? detail = null) => Save(status, new { state, target, kind, captureId = id, processId = Environment.ProcessId, time = DateTimeOffset.UtcNow, detail, midiInput = input.Name, midiOutput = false, codexActions = false });
        try
        {
            draft.State = "armed"; draft.Target = target; draft.CaptureId = id; draft.Revision++; Save(path, draft);
            Status("armed_waiting"); Console.WriteLine($"READY {target}; input-only; waiting without pre-gesture timeout; cancel via {cancel}");
            while (!stop.IsCancellationRequested && !File.Exists(cancel))
            {
                if (midi.Dropped != 0 || midi.InputError) throw new IOException("MIDI input loss; candidate not accepted");
                while (midi.TryRead(out var packet))
                {
                    long now = clock.ElapsedMilliseconds;
                    long age = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency) - packet.ReceivedMs;
                    if (age is < 0 or > 200) throw new IOException("Stale MIDI input; repeat stage");
                    var sample = LearnPolicy.Sample(packet.Message, now);
                    // Active sensing / timing are not physical choices and never arm a gesture.
                    if (packet.Message.Status >= 0xF8) continue;
                    if (first < 0) { first = now; Status("collecting"); }
                    last = now; samples.Add(sample); raw.WriteLine(JsonSerializer.Serialize(sample, Json).Replace("\r", "").Replace("\n", ""));
                    if (sample.Type == "note") { if (sample.Edge == "down") held.Add((sample.Channel, sample.Number)); else held.Remove((sample.Channel, sample.Number)); }
                    if (samples.Count >= 2048) throw new IOException("Gesture sample limit; raw evidence preserved, repeat stage");
                }
                if (first >= 0 && ((held.Count == 0 && clock.ElapsedMilliseconds - last >= 800) || clock.ElapsedMilliseconds - first >= 20000))
                {
                    var candidate = LearnPolicy.Analyze(id, target, kind, samples, draft.Associations);
                    draft.Candidates.Add(candidate); draft.State = candidate.Issues.Count == 0 ? "candidate_review" : "ambiguous"; draft.Revision++; Save(path, draft);
                    Status(draft.State, candidate); Console.WriteLine(JsonSerializer.Serialize(candidate, Json));
                    return candidate.Issues.Count == 0 ? 0 : 2;
                }
                await Task.Delay(10, stop.Token);
            }
            draft.State = "cancelled"; draft.Revision++; Save(path, draft); Status("cancelled"); return 3;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { draft.State = "cancelled"; draft.Revision++; Save(path, draft); Status("cancelled"); return 3; }
        catch (Exception e)
        { draft.State = "error"; draft.Revision++; Save(path, draft); Status("error", e.Message); throw; }
        finally { Console.CancelKeyPress -= handler; }
    }
}
