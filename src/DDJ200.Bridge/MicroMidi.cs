using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Ddj200;

public sealed record MicroBinding(string Slot, string Fingerprint)
{
    public static bool IsSlot(string slot) => slot is "ACT06" or "ACT07" or "ACT08" or "ACT09" or "ACT10" or "ACT11" or "ACT12";
    public static string StatePath => Environment.GetEnvironmentVariable("DDJ_CODEX_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    public static MicroBinding Read(string slot) => ParseToml(File.ReadAllText(StatePath), slot);
    // Deliberately accepts only the scalar/table form emitted by the installed app.
    // Unknown TOML syntax in this layout is refused rather than guessed.
    public static MicroBinding ParseToml(string toml, string slot)
        => Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["codex-micro-layout"] = ParseLayoutToml(toml) }), slot);
    public static Dictionary<string, object> ParseLayoutToml(string toml)
    {
        var layout = new Dictionary<string, object>();
        Dictionary<string, object>? current = null;
        var tables = new HashSet<string>();
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                current = null;
                if (!line.StartsWith("[desktop.codex-micro-layout")) continue;
                if (!line.EndsWith(']') || line.StartsWith("[[")) throw new InvalidDataException("Unsupported layout table");
                string table = line[1..^1];
                if (table != "desktop.codex-micro-layout" && !table.StartsWith("desktop.codex-micro-layout.")) throw new InvalidDataException("Ambiguous layout table");
                if (!tables.Add(table)) throw new InvalidDataException("Duplicate layout table");
                current = layout;
                foreach (string part in table.Split('.').Skip(2))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(part, "^[A-Za-z0-9_-]+$")) throw new InvalidDataException("Unsupported table name");
                    if (!current.TryGetValue(part, out var child)) current[part] = child = new Dictionary<string, object>();
                    current = child as Dictionary<string, object> ?? throw new InvalidDataException("Conflicting layout value");
                }
                continue;
            }
            if (current == null) continue;
            int equals = line.IndexOf('=');
            if (equals < 1) throw new InvalidDataException("Unsupported layout assignment");
            string key = line[..equals].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]+$")) throw new InvalidDataException("Unsupported layout key");
            using var scalar = JsonDocument.Parse(line[(equals + 1)..].Trim());
            if (scalar.RootElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Unsupported layout scalar");
            if (!current.TryAdd(key, scalar.RootElement.Clone())) throw new InvalidDataException("Duplicate layout value");
        }
        return layout;
    }
    public static MicroBinding Parse(string json, string slot)
    {
        if (!IsSlot(slot)) throw new InvalidDataException("Choose an explicit ACT06..ACT12 slot");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("codex-micro-layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("No explicit Micro layout saved; default slots are not settings");
        if (!layout.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v != 1 ||
            !layout.TryGetProperty("slots", out var slots) || !slots.TryGetProperty(slot, out var binding) || binding.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing or unsupported Micro slot configuration");
        if (slot is "ACT10" or "ACT11" && (!layout.TryGetProperty("separateMicrophoneKeys", out var separate) || separate.ValueKind != JsonValueKind.True))
            throw new InvalidDataException("Combined microphone slots cannot be used for this test");
        string? command = null;
        if (binding.TryGetProperty("action", out var action))
        {
            if (action.ValueKind != JsonValueKind.Object || !action.TryGetProperty("type", out var type) || type.GetString() != "command" ||
                !action.TryGetProperty("commandId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Slot has a non-command override");
            command = id.GetString();
        }
        else if (binding.TryGetProperty("commandId", out var id)) command = id.ValueKind == JsonValueKind.String ? id.GetString() : "invalid";
        else if (binding.TryGetProperty("keycapId", out var keycap) && keycap.ValueKind == JsonValueKind.String && keycap.GetString() is "SETUP" or "LAB") command = "settings";
        if (command != "settings") throw new InvalidDataException("Selected Micro slot is not configured to open settings");
        return new(slot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.GetRawText()))));
    }
}

// Pure, clock-injected state machine. No MIDI, UI, filesystem or network access here.
public sealed class MicroMidiLogic
{
    public static readonly MidiMessage PlayOn = new(0x90, 0x0B, 127), PlayOff = new(0x90, 0x0B, 0);
    private readonly long deadline;
    private long lightingAt = long.MinValue, feedbackAt = long.MinValue, downAt = -1, lastAction = -1000, quietUntil;
    private bool? desiredLed;
    private bool ledWritten, lastLed, stopped;
    public bool Ready(long now, bool connected) => !stopped && connected && now < deadline && lightingAt != long.MinValue && now - feedbackAt <= 15000;
    public MicroMidiLogic(long deadlineMs) { deadline = deadlineMs; }
    public void Lighting(string method, JsonElement parameters, long now)
    {
        if (stopped) return;
        // Traffic freshness only; thread status never supplies an LED value or task meaning.
        if (method is "v.oai.rgbcfg" or "v.oai.thstatus") feedbackAt = now;
        if (method != "v.oai.rgbcfg") return;
        if (!parameters.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object ||
            !keys.TryGetProperty("b", out var b) || !b.TryGetDouble(out double brightness) || !double.IsFinite(brightness) || brightness is < 0 or > 1 ||
            !keys.TryGetProperty("e", out var e) || !e.TryGetInt32(out int effect) || effect is not (0 or 1) ||
            !keys.TryGetProperty("c", out var c) || !c.TryGetInt32(out int color) || color is < 0 or > 0xFFFFFF)
        { Stop(); throw new InvalidDataException("Unsupported keys lighting; expected e=off/solid, b=0..1, c=RGB24"); }
        lightingAt = now;
        desiredLed = effect == 1 && brightness > 0 && color != 0;
    }
    public MidiMessage? NextLed(long now, bool connected)
    {
        bool value = Ready(now, connected) && desiredLed == true;
        if (desiredLed == null && !ledWritten) return null;
        return !ledWritten || value != lastLed ? value ? PlayOn : PlayOff : null;
    }
    public void SentLed(MidiMessage message, long now)
    {
        if (message != PlayOn && message != PlayOff) throw new InvalidOperationException("Only left PLAY LED is permitted");
        ledWritten = true; lastLed = message == PlayOn; quietUntil = now + 150; downAt = -1;
    }
    // One complete physical press/release creates one action, never a timer-generated action.
    public bool Input(MidiMessage message, long now, bool connected)
    {
        if (!Ready(now, connected) || now <= quietUntil) { downAt = -1; return false; }
        if (message.Data1 != 0x0B || message.Status is not (0x90 or 0x80) || message.Data2 > 127) return false;
        bool down = message.Status == 0x90 && message.Data2 > 0;
        if (down) { if (downAt < 0) downAt = now; return false; }
        long held = downAt < 0 ? -1 : now - downAt; downAt = -1;
        if (held is < 20 or > 3000 || now - lastAction < 250) return false;
        lastAction = now; return true;
    }
    public void Stop() { stopped = true; downAt = -1; desiredLed = ledWritten ? false : null; }
}

public static class MicroMidi
{
    public static async Task<int> Run(string slot, int seconds, bool arm)
    {
        if (!arm) throw new ArgumentException("Physical mode requires explicit --arm after coordinated authorization");
        if (seconds is < 10 or > 60) throw new ArgumentException("Physical window must be 10..60 seconds");
        var binding = MicroBinding.Read(slot); // Before opening any hardware or socket.
        using var owner = new DeviceOwnership();
        var input = MidiDevice.Select(MidiDevice.Ports(false), "DDJ-200");
        var output = MidiDevice.Select(MidiDevice.Ports(true), "DDJ-200");
        var events = Channel.CreateBounded<(string Method, JsonElement Value, long Time)>(64);
        int overflow = 0;
        var clock = Stopwatch.StartNew();
        var logic = new MicroMidiLogic(seconds * 1000L);
        await using var server = new MicroUsbIp();
        server.LightingReceived += (method, value) => { if (!events.Writer.TryWrite((method, value, clock.ElapsedMilliseconds))) Interlocked.Exchange(ref overflow, 1); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        using var midi = new MidiDevice(input, output);
        bool wasConnected = false;
        bool actionSent = false;
        long lastGuard = 0;
        void CheckBinding()
        {
            if (MicroBinding.Read(slot) != binding) throw new InvalidOperationException("Micro layout changed; disarmed");
        }
        void ApplyLed()
        {
            if (logic.NextLed(clock.ElapsedMilliseconds, server.IsImported) is not { } next) return;
            midi.Send(next); logic.SentLed(next, clock.ElapsedMilliseconds);
            Console.WriteLine($"LED {next.Hex}; source=real-rgbcfg-or-safe-cleanup");
        }
        try
        {
            CheckBinding(); server.Start();
            Console.WriteLine($"ARMED {seconds}s; {slot}=settings; left PLAY press/release only. Awaiting one-time external attach and valid keys lighting.");
            while (!stop.IsCancellationRequested)
            {
                bool connected = server.IsImported;
                if (wasConnected && !connected) throw new IOException("USB/IP disconnected; disarmed");
                wasConnected |= connected;
                if (overflow != 0 || midi.Dropped > 0 || midi.InputError) throw new IOException("Input/feedback loss; disarmed");
                if (clock.ElapsedMilliseconds - lastGuard >= 250) { CheckBinding(); lastGuard = clock.ElapsedMilliseconds; }
                while (events.Reader.TryRead(out var item))
                {
                    logic.Lighting(item.Method, item.Value, item.Time);
                    if (item.Method == "v.oai.rgbcfg") Console.WriteLine($"{clock.ElapsedMilliseconds}ms validated real keys lighting: {item.Value.GetProperty("keys").GetRawText()}");
                }
                ApplyLed();
                while (midi.TryRead(out var packet))
                {
                    // WinMM callback timestamps use the same monotonic clock domain.
                    long packetAge = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency) - packet.ReceivedMs;
                    if (packetAge is < 0 or > 150) throw new IOException("Stale MIDI packet; disarmed");
                    if (packet.Message.Data1 == 0x0B && packet.Message.Status is 0x90 or 0x80)
                        Console.WriteLine($"{clock.ElapsedMilliseconds}ms physical PLAY input {packet.Message.Hex}; ready={logic.Ready(clock.ElapsedMilliseconds, connected)}; actionAlreadySent={actionSent}");
                    if (actionSent) continue;
                    if (!logic.Input(packet.Message, clock.ElapsedMilliseconds, connected)) continue;
                    CheckBinding();
                    if (!server.TrySendSettingsKey(binding.Slot)) throw new IOException("No active HID reader; action not queued");
                    actionSent = true;
                    Console.WriteLine($"Physical left PLAY cycle queued v.oai.hid {binding.Slot} press/release; command=settings; app effect requires observation");
                }
                await Task.Delay(10, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            logic.Stop();
            try { ApplyLed(); }
            catch (Exception ex) { Console.Error.WriteLine($"LED cleanup unconfirmed: {ex.Message}"); }
            Console.CancelKeyPress -= handler;
            Console.WriteLine("DISARMED. Close/detach only the recorded virtual port; no reconnect is armed.");
        }
        return 0;
    }
}
