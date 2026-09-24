using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ddj200;

public sealed record ProductButton(string Target, string Control, string Signature);
public sealed record ProductJog(double LeftScale, double RightScale);
public sealed record ProductLedPattern(string Mode, IReadOnlyList<int> Durations);
public sealed record ProductProfile(int SchemaVersion, IReadOnlyList<ProductButton> Buttons, IReadOnlyDictionary<string,string> Analogs, ProductJog Jog, IReadOnlyDictionary<string,ProductLedPattern>? LedPatterns = null)
{
    public static string DataRoot => Environment.GetEnvironmentVariable("DDJ_BRIDGE_DATA") ?? Directory.GetCurrentDirectory();
    public static string RuntimeFolder => Path.Combine(DataRoot, "runtime", "surface12");
    public static string ProfilePath => Environment.GetEnvironmentVariable("DDJ_BRIDGE_PROFILE") ?? Path.Combine(AppContext.BaseDirectory, "config", "controller-profile.json");

    public static ProductProfile Load()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ProfilePath))!.AsObject();
        ProfileMigration.Upgrade(node);
        var profile = JsonSerializer.Deserialize<ProductProfile>(node.ToJsonString(), MidiLearn.Json)
            ?? throw new InvalidDataException("Invalid controller product profile");
        if (profile.SchemaVersion != 1 || profile.Jog is null || profile.Buttons is null || profile.Analogs is null)
            throw new InvalidDataException("Unsupported controller product profile");
        ProductLedPolicy.Validate(profile.EffectiveLedPatterns);
        return profile;
    }

    public IReadOnlyDictionary<string,ProductLedPattern> EffectiveLedPatterns => LedPatterns ?? ProductLedPolicy.Default;

    public static string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(ProfilePath))));
}


public static class ProductLedPolicy
{
    public static readonly IReadOnlyDictionary<string,ProductLedPattern> Default = new Dictionary<string,ProductLedPattern>
    {
        ["selected"] = new("blink", [2000,100]),
        ["working"] = new("blink", [1000,500]),
        ["unread"] = new("solidOn", []),
        ["idle"] = new("solidOff", []),
        ["unassigned"] = new("solidOff", []),
        ["attention"] = new("blink", [250,250]),
        ["error"] = new("blink", [250,250,250,250,250,1000]),
        ["commandConfigured"] = new("solidOn", []),
        ["commandHold"] = new("blink", [500,500]),
        ["commandReleased"] = new("solidOn", [])
    };
    private static readonly string[] Required = ["selected","working","unread","idle","unassigned","attention","error","commandConfigured","commandHold","commandReleased"];

    public static void Validate(IReadOnlyDictionary<string,ProductLedPattern> patterns)
    {
        if (patterns.Count != Required.Length || !patterns.Keys.ToHashSet().SetEquals(Required))
            throw new InvalidDataException("LED profile requires exactly the supported named states");
        foreach (var (name, pattern) in patterns)
        {
            if (pattern.Mode is "solidOn" or "solidOff")
            {
                if (pattern.Durations.Count != 0) throw new InvalidDataException($"Solid LED state {name} cannot contain timings");
            }
            else if (pattern.Mode == "blink")
            {
                if (pattern.Durations.Count < 2 || pattern.Durations.Count % 2 != 0 || pattern.Durations.Any(x => x is < 50 or > 5000))
                    throw new InvalidDataException($"Blink LED state {name} requires alternating 50..5000 ms on/off timings");
            }
            else throw new InvalidDataException($"Unsupported LED mode for {name}");
        }
    }

    public static bool IsOn(ProductLedPattern pattern, long elapsed)
    {
        if (pattern.Mode == "solidOn") return true;
        if (pattern.Mode == "solidOff") return false;
        long cycle = pattern.Durations.Sum();
        long position = Math.Max(0, elapsed) % cycle;
        for (int i=0;i<pattern.Durations.Count;i++)
        {
            if (position < pattern.Durations[i]) return i % 2 == 0;
            position -= pattern.Durations[i];
        }
        return false;
    }
}
