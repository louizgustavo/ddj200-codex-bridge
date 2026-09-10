using System.Text.Json;

namespace Ddj200;

public static class MicroMidiTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException("FAIL " + description); Console.WriteLine("PASS " + description); passed++; }
        void Reject(string json, string slot = "ACT11")
        {
            bool rejected = false;
            try { MicroBinding.Parse(json, slot); } catch (Exception e) when (e is InvalidDataException or JsonException) { rejected = true; }
            Check(rejected, "binding refuses " + json);
        }
        string Layout(string slotValue) => "{\"codex-micro-layout\":{\"version\":1,\"separateMicrophoneKeys\":true,\"slots\":{\"ACT11\":" + slotValue + "}}}";
        Reject("{}");
        Reject(Layout("{\"keycapId\":\"CODEX\"}"));
        Reject(Layout("{\"keycapId\":\"SETUP\",\"commandId\":\"approval.approve\"}"));
        Reject(Layout("{\"keycapId\":\"SETUP\",\"action\":{\"type\":\"skill\"}}"));
        Reject(Layout("{\"keycapId\":\"SETUP\"}").Replace("true", "false"));
        var valid = MicroBinding.Parse(Layout("{\"keycapId\":\"SETUP\"}"), "ACT11");
        Check(valid.Slot == "ACT11", "explicit settings slot accepted");
        Check(MicroBinding.Parse(Layout("{\"keycapId\":\"FAST\",\"commandId\":\"settings\"}"), "ACT11").Slot == "ACT11", "verified settings command override accepted");
        Check(valid.Fingerprint != MicroBinding.Parse(Layout("{\"keycapId\":\"LAB\"}"), "ACT11").Fingerprint, "layout changes invalidate captured binding");
        string toml = "[desktop.codex-micro-layout]\nversion = 1\n[desktop.codex-micro-layout.slots.ACT06]\nkeycapId = \"SETUP\"\n[unrelated]\nvalue = 'ignored'";
        Check(MicroBinding.ParseToml(toml, "ACT06").Slot == "ACT06", "installed desktop TOML layout accepted independently of unrelated config");
        bool duplicateRejected = false;
        try { MicroBinding.ParseToml(toml.Replace("keycapId = \"SETUP\"", "keycapId = \"SETUP\"\nkeycapId = \"FAST\""), "ACT06"); } catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected, "ambiguous duplicate TOML binding fails closed");

        var on = JsonDocument.Parse("{\"keys\":{\"e\":1,\"b\":0.25,\"c\":16777215}}").RootElement;
        var off = JsonDocument.Parse("{\"keys\":{\"e\":0,\"b\":0.8,\"c\":16777215}}").RootElement;
        var logic = new MicroMidiLogic(30000);
        Check(logic.NextLed(1, true) == null && !logic.Input(MicroMidiLogic.PlayOn, 1, true), "no lighting or action before real feedback");
        logic.Lighting("v.oai.thstatus", JsonDocument.Parse("[{\"id\":0,\"b\":1}]").RootElement, 50);
        Check(logic.NextLed(50, true) == null, "thread status cannot drive PLAY feedback");
        logic.Lighting("v.oai.rgbcfg", on, 100);
        Check(logic.NextLed(100, true) == MicroMidiLogic.PlayOn, "normalized nonzero solid key brightness maps to binary on");
        logic.SentLed(MicroMidiLogic.PlayOn, 100);
        Check(!logic.Input(MicroMidiLogic.PlayOn, 110, true) && !logic.Input(MicroMidiLogic.PlayOff, 150, true), "LED echo cannot create a physical action");
        Check(!logic.Input(MicroMidiLogic.PlayOn, 300, true) && !logic.Input(MicroMidiLogic.PlayOn, 320, true) && logic.Input(MicroMidiLogic.PlayOff, 400, true), "one complete physical cycle despite repeated note-on");
        Check(!logic.Input(MicroMidiLogic.PlayOff, 410, true), "duplicate release emits no second action");
        logic.Input(MicroMidiLogic.PlayOn, 450, true);
        Check(!logic.Input(MicroMidiLogic.PlayOff, 480, true), "rapid bounce is suppressed");
        logic.Input(new MidiMessage(0x91, 0x0B, 127), 800, true);
        Check(!logic.Input(new MidiMessage(0x81, 0x0B, 0), 850, true), "right PLAY is not mapped");
        logic.Input(MicroMidiLogic.PlayOn, 1000, true);
        Check(logic.Input(new MidiMessage(0x80, 0x0B, 45), 1050, true), "real note-off with release velocity completes one cycle");
        Check(logic.NextLed(1100, true) == null, "unchanged lighting causes no duplicate MIDI output");
        logic.Lighting("v.oai.rgbcfg", off, 1200);
        Check(logic.NextLed(1200, true) == MicroMidiLogic.PlayOff, "off effect dominates nonzero brightness");
        logic.Lighting("v.oai.rgbcfg", on, 2000);
        logic.Input(MicroMidiLogic.PlayOn, 2200, true);
        Check(!logic.Input(MicroMidiLogic.PlayOff, 2250, false) && logic.NextLed(2250, false) == MicroMidiLogic.PlayOff, "disconnect prevents action and clears owned light");
        Check(logic.NextLed(18000, true) == MicroMidiLogic.PlayOff && !logic.Ready(18000, true), "stale lighting degrades to off and disarms input");
        logic.Lighting("v.oai.thstatus", JsonDocument.Parse("[]").RootElement, 18001);
        Check(logic.Ready(18002, true) && logic.NextLed(18002, true) == null, "live traffic preserves confirmed lighting without deriving LED state from thread status");
        logic.Lighting("v.oai.rgbcfg", on, 29900);
        logic.Input(MicroMidiLogic.PlayOn, 29950, true);
        Check(!logic.Input(MicroMidiLogic.PlayOff, 30000, true), "deadline cancels a held physical gesture");
        logic.Stop();
        Check(logic.NextLed(30001, true) == MicroMidiLogic.PlayOff, "cleanup only requests owned LED off");
        foreach (string bad in new[] { "{\"keys\":{\"e\":1,\"b\":128,\"c\":1}}", "{\"keys\":{\"e\":2,\"b\":1,\"c\":1}}", "{\"keys\":{\"e\":1,\"b\":1}}" })
        {
            var candidate = new MicroMidiLogic(30000); bool rejected = false;
            try { candidate.Lighting("v.oai.rgbcfg", JsonDocument.Parse(bad).RootElement, 10); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !candidate.Ready(10, true), "unknown lighting scale/effect/color fails closed");
        }
        string testOwnerName="Local\\DDJ200.Test."+Guid.NewGuid().ToString("N");
        using (var first = new DeviceOwnership(testOwnerName))
        {
            bool refused = false;
            try { using var second = new DeviceOwnership(testOwnerName); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "second bridge cannot acquire device ownership");
        }
        using (var reacquired = new DeviceOwnership(testOwnerName)) Check(true, "device ownership released after scope");
        return passed;
    }
}
