using System.Text;
using System.Text.Json;

namespace Ddj200;

// Original experimental protocol implementation. No installed SDK is imported or copied.
public sealed class MicroProtocol
{
    public const int ReportSize = 64, PayloadSize = 61, MaxJsonBytes = 8192;
    private readonly List<byte> incoming = new();
    public event Action<string, JsonElement>? LightingReceived;

    public IReadOnlyList<byte[]> Accept(byte[] report)
    {
        if (report.Length != ReportSize || report[0] != 6 || report[1] != 2 || report[2] > PayloadSize)
        { incoming.Clear(); throw new InvalidDataException("Invalid Micro RPC report"); }
        if (incoming.Count + report[2] > MaxJsonBytes) { incoming.Clear(); throw new InvalidDataException("Micro JSON limit"); }
        incoming.AddRange(report.AsSpan(3, report[2]).ToArray());
        // Host requests have no newline. A streaming JSON parser distinguishes an
        // incomplete value from malformed JSON, including across UTF-8 boundaries.
        var bytes = incoming.ToArray();
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out var document)) return Array.Empty<byte[]>();
            using (document)
            {
                if (bytes.AsSpan((int)reader.BytesConsumed).ToArray().Any(b => b is not (9 or 10 or 13 or 32)))
                    throw new InvalidDataException("One RPC value per request required");
                incoming.Clear();
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("RPC method required");
                string method = methodValue.GetString()!;
                object? result = null;
                object? error = null;
                switch (method)
                {
                    case "device.status":
                        LightingReceived?.Invoke(method, JsonSerializer.SerializeToElement(new { }));
                        result = new { version = "ddj-micro-experimental-0.1" };
                        break;
                    case "v.oai.rgbcfg":
                    case "v.oai.thstatus":
                        if (!root.TryGetProperty("params", out var parameters) ||
                            (method == "v.oai.rgbcfg" ? parameters.ValueKind != JsonValueKind.Object : parameters.ValueKind != JsonValueKind.Array))
                            error = new { code = -32602, message = "Invalid lighting parameters" };
                        else
                        {
                            LightingReceived?.Invoke(method, parameters.Clone());
                            result = true; // Protocol receipt; physical MIDI is separately gated.
                        }
                        break;
                    default:
                        error = new { code = -32601, message = "Method not supported" };
                        break;
                }
                if (!root.TryGetProperty("id", out var id)) return Array.Empty<byte[]>();
                return Encode(error == null ? new { id = id.Clone(), result } : (object)new { id = id.Clone(), error });
            }
        }
        catch (JsonException ex) { incoming.Clear(); throw new InvalidDataException("Malformed Micro JSON", ex); }
    }

    public static IReadOnlyList<byte[]> Encode(object value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
        if (bytes.Length > MaxJsonBytes) throw new InvalidDataException("Micro response limit");
        var reports = new List<byte[]>();
        for (int i = 0; i < bytes.Length; i += PayloadSize)
        {
            int count = Math.Min(PayloadSize, bytes.Length - i);
            var report = new byte[ReportSize];
            report[0] = 6; report[1] = 2; report[2] = (byte)count;
            bytes.AsSpan(i, count).CopyTo(report.AsSpan(3));
            reports.Add(report);
        }
        return reports;
    }
}

public static class MicroDescriptors
{
    public const ushort Vendor = 0x303A, Product = 0x8297;
    // Vendor-defined collection only: no keyboard, mouse, or consumer-control interface.
    public static byte[] Report => new byte[] {
        0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x06,
        0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x3F,
        0x09, 0x01, 0x81, 0x02, 0x09, 0x01, 0x91, 0x02, 0xC0 };
    public static byte[] Device => new byte[] {
        18, 1, 0, 2, 0, 0, 0, 64, 0x3A, 0x30, 0x97, 0x82, 0, 1, 1, 2, 3, 1 };
    public static byte[] Hid => new byte[] { 9, 0x21, 0x11, 1, 0, 1, 0x22, (byte)Report.Length, 0 };
    public static byte[] Configuration => new byte[] { 9, 2, 41, 0, 1, 1, 0, 0x80, 50,
        9, 4, 0, 0, 2, 3, 0, 0, 0 }.Concat(Hid).Concat(new byte[] {
        7, 5, 0x81, 3, 64, 0, 4, 7, 5, 0x01, 3, 64, 0, 4 }).ToArray();
    public static byte[]? Get(int type, int index) => (type, index) switch
    {
        (1, 0) => Device, (2, 0) => Configuration, (0x21, 0) => Hid, (0x22, 0) => Report,
        (3, 0) => new byte[] { 4, 3, 9, 4 },
        (3, 1) => String("DDJ experimental bridge"),
        (3, 2) => String("DDJ Micro Bridge (experimental)"),
        (3, 3) => String("DDJ-BRIDGE-0001"),
        _ => null
    };
    private static byte[] String(string value)
    {
        var text = Encoding.Unicode.GetBytes(value);
        return new byte[] { (byte)(text.Length + 2), 3 }.Concat(text).ToArray();
    }
}
