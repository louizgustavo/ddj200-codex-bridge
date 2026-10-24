using System.Text.Json.Nodes;

namespace Ddj200;

public static class ProfileMigration
{
    public static bool Upgrade(JsonObject profile)
    {
        var buttons = profile["buttons"]!.AsArray();
        if (buttons.Count != 12) return false;
        var analogs = profile["analogs"]!.AsObject();
        if (analogs["joystick.up"]?.GetValue<string>() != "absolute14:2:0:negative_from_center" ||
            analogs["joystick.down"]?.GetValue<string>() != "absolute14:2:0:positive_from_center")
            throw new InvalidDataException("Unknown legacy vertical mapping; cannot migrate automatically.");
        if (buttons.Any(b => b!["control"]!.GetValue<string>() is "deck1.play" or "deck1.cue"))
            throw new InvalidDataException("Left PLAY/CUE already assigned; cannot migrate automatically.");
        buttons.Add(new JsonObject { ["target"]="joystick.down", ["control"]="deck1.play", ["signature"]="note:1:11" });
        buttons.Add(new JsonObject { ["target"]="joystick.up", ["control"]="deck1.cue", ["signature"]="note:1:12" });
        analogs.Remove("joystick.up");
        analogs.Remove("joystick.down");
        var hold = profile["ledPatterns"]?["commandHold"];
        if (hold?["mode"]?.GetValue<string>() == "blink" &&
            hold["durations"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual(new[] { 1000, 1000 }))
            hold["durations"] = new JsonArray(500, 500);
        return true;
    }
}
