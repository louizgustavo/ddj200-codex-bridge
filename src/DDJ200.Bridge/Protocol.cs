namespace Ddj200;

public record ControlEvent(string Control, string Kind, int Value, string Raw);
public sealed class Decoder
{
    private readonly Dictionary<(int, int), (byte Value, long Time)> msbs = new();
    private static readonly Dictionary<int, string> Notes = new()
    {
        [0x0B] = "play", [0x47] = "shift.play", [0x0C] = "cue", [0x48] = "shift.cue",
        [0x3F] = "shift", [0x36] = "jog.touch", [0x67] = "shift.jog.touch",
        [0x58] = "sync", [0x5C] = "sync.hold", [0x60] = "shift.sync",
        [0x54] = "headphones", [0x68] = "shift.headphones", [0x66] = "fader_start.play", [0x52] = "fader_start.cue"
    };
    public ControlEvent? Decode(MidiMessage m, long milliseconds)
    {
        int channel = m.Status & 15, type = m.Status & 0xF0;
        if (m.Data1 > 127 || m.Data2 > 127) return null;
        if (type is 0x90 or 0x80)
        {
            string? name = null;
            if (channel is 0 or 1 && Notes.TryGetValue(m.Data1, out var note)) name = $"deck{channel + 1}.{note}";
            if (channel is 7 or 8 or 9 or 10 && m.Data1 < 8)
                name = $"deck{(channel < 9 ? 1 : 2)}.{(channel is 8 or 10 ? "shift." : "")}pad{m.Data1 + 1}";
            if (channel == 6) name = m.Data1 switch
            {
                0x63 => "master.cue", 0x78 => "master.shift.cue", 0x59 => "transition", 0x5A => "shift.transition", _ => null
            };
            return name == null ? null : new(name, "button", type == 0x80 || m.Data2 == 0 ? 0 : 1, m.Hex);
        }
        if (type != 0xB0) return null;
        if (channel is 0 or 1 && m.Data1 is 0x21 or 0x22 or 0x23 or 0x29)
            return new($"deck{channel + 1}.jog.{m.Data1:X2}", "relative", m.Data2 - 64, m.Hex);
        int cc = m.Data1 >= 32 ? m.Data1 - 32 : m.Data1;
        string? control = channel is 0 or 1 ? cc switch
        {
            0 => "tempo", 7 => "eq.high", 11 => "eq.mid", 15 => "eq.low", 19 => "fader", _ => null
        } : channel == 6 ? cc switch { 23 => "deck1.filter", 24 => "deck2.filter", 31 => "crossfader", _ => null } : null;
        if (control == null) return null;
        var key = (channel, cc);
        if (m.Data1 < 32) { msbs[key] = (m.Data2, milliseconds); return null; }
        // Discard orphan/stale LSBs; do not emit two actions for one 14-bit value.
        if (!msbs.Remove(key, out var msb) || milliseconds - msb.Time is < 0 or > 100) return null;
        return new(channel is 0 or 1 ? $"deck{channel + 1}.{control}" : control, "absolute14", msb.Value * 128 + m.Data2, m.Hex);
    }
}

public static class LedPolicy
{
    // Only ordinary documented on/off notes; no SysEx, vinyl-mode changes, or load animations.
    public static bool IsAllowed(MidiMessage m) => m.Data2 is 0 or 127 &&
        ((m.Status is 0x90 or 0x91 && m.Data1 is 0x0B or 0x0C or 0x58 or 0x54) ||
         (m.Status is 0x97 or 0x99 && m.Data1 < 8) || (m.Status == 0x96 && m.Data1 is 0x63 or 0x59));
    public static IReadOnlyDictionary<string, MidiMessage> Targets { get; } = MakeTargets();
    private static Dictionary<string, MidiMessage> MakeTargets()
    {
        var targets = new Dictionary<string, MidiMessage>();
        for (int deck = 1; deck <= 2; deck++)
        {
            foreach (var (name, note) in new[] { ("play", 11), ("cue", 12), ("sync", 88), ("headphones", 84) })
                targets[$"deck{deck}.{name}"] = new((byte)(0x8F + deck), (byte)note, 127);
            for (int pad = 1; pad <= 8; pad++) targets[$"deck{deck}.pad{pad}"] = new((byte)(deck == 1 ? 0x97 : 0x99), (byte)(pad - 1), 127);
        }
        targets["master.cue"] = new(0x96, 0x63, 127);
        targets["transition"] = new(0x96, 0x59, 127);
        return targets;
    }
}

public sealed class EchoGuard
{
    private readonly Dictionary<uint, long> sent = new();
    public void Sent(MidiMessage m, long now) => sent[m.Packed] = now;
    public bool IsEcho(MidiMessage m, long now)
    {
        foreach (var key in sent.Where(p => now - p.Value > 150).Select(p => p.Key).ToArray()) sent.Remove(key);
        if ((m.Status & 0xF0) == 0x80) m = new((byte)(0x90 | (m.Status & 15)), m.Data1, 0);
        return sent.TryGetValue(m.Packed, out var time) && now - time is >= 0 and <= 150;
    }
}
