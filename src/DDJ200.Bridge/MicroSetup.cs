using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ddj200;

// Setup is deliberately independent of MIDI and of a previously saved Micro layout.
public static class MicroSetup
{
    public static string Defaults => """
        [desktop.codex-micro-layout]
        version = 1
        separateMicrophoneKeys = false
        voiceButtonMode = "push-to-talk"
        encoderMode = "composer-navigation"

        [desktop.codex-micro-layout.slots.ACT06]
        keycapId = "FAST"
        [desktop.codex-micro-layout.slots.ACT07]
        keycapId = "APPR"
        [desktop.codex-micro-layout.slots.ACT08]
        keycapId = "REJ"
        [desktop.codex-micro-layout.slots.ACT09]
        keycapId = "SPLIT"
        [desktop.codex-micro-layout.slots.ACT10_ACT11]
        keycapId = "MIC"
        [desktop.codex-micro-layout.slots.ACT12]
        keycapId = "CODEX"
        [desktop.codex-micro-layout.analogStick.up]
        type = "command"
        commandId = "composer.togglePlanMode"
        [desktop.codex-micro-layout.analogStick.right]
        type = "command"
        commandId = "navigateForward"
        [desktop.codex-micro-layout.analogStick.down]
        type = "command"
        commandId = "toggleSidebar"
        [desktop.codex-micro-layout.analogStick.left]
        type = "command"
        commandId = "navigateBack"
        """;

    public static string PrepareText(string original)
    {
        if (Tomlyn.Toml.Parse(original).HasErrors)
            throw new InvalidDataException("O arquivo de configuração não é TOML válido. Corrija-o no Codex antes de preparar o Micro; o arquivo foi preservado.");
        // Never rewrite existing preferences. Refuse ambiguous syntax instead of
        // appending a second representation of the same TOML key/table.
        var layout = MicroBinding.ParseLayoutToml(original);
        bool hasLayout = original.Contains("codex-micro-layout", StringComparison.Ordinal);
        bool hasSource = original.Contains("codex-micro-agent-source", StringComparison.Ordinal);
        if ((!hasSource || !hasLayout) && (original.Contains("\"\"\"") || original.Contains("'''")))
            throw new InvalidDataException("A configuração usa texto TOML multilinha; o assistente preservou o arquivo. Salve o layout nas configurações do Codex Micro.");
        string next = original;
        if (!hasSource)
        {
            var desktops = Regex.Matches(next, @"(?m)^\s*\[desktop\][ \t]*(?:#[^\r\n]*)?\r?$");
            if (desktops.Count > 1) throw new InvalidDataException("Há tabelas desktop duplicadas; a configuração foi preservada.");
            if (desktops.Count == 1)
            {
                int at = desktops[0].Index + desktops[0].Length;
                next = next.Insert(at, "\ncodex-micro-agent-source = \"recent\"");
            }
            else
            {
                if (Regex.IsMatch(next, @"(?m)^\s*(desktop\s*=|desktop\.|\[\s*[""']desktop)"))
                    throw new InvalidDataException("Formato desktop não suportado pelo assistente; nenhuma preferência foi substituída.");
                next += "\n[desktop]\ncodex-micro-agent-source = \"recent\"\n";
            }
        }
        if (!hasLayout) next += "\n" + Defaults + "\n";
        else if (layout.Count == 0) throw new InvalidDataException("Layout Micro existente não reconhecido; configuração preservada.");
        if (Tomlyn.Toml.Parse(next).HasErrors)
            throw new InvalidDataException("Não foi possível acrescentar as configurações Micro sem conflito TOML. O arquivo foi preservado.");
        Validate(next);
        return next;
    }

    public static void Validate(string text)
    {
        try
        {
            Surface12Map.ConfigurationFingerprint(text, nativeRemapping: true);
            AnalogSurfaceMap.ValidateLayout(text, allowNativeRemapping: true);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException("A configuração Micro existente é incompatível. Preserve sua fonte (recentes ou fixados) e confira em Configurações > Codex Micro: layout v1, microfone combinado/push-to-talk e direções com ações de comando. Nenhuma preferência existente foi substituída.", e);
        }
    }

    public static bool PrepareFile(string path)
    {
        // A separate handle denies concurrent writers during validation/replacement.
        bool exists = File.Exists(path);
        byte[] before = exists ? File.ReadAllBytes(path) : Array.Empty<byte>();
        string original = new UTF8Encoding(false, true).GetString(before).TrimStart('\uFEFF');
        string next = PrepareText(original);
        if (next == original) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".ddj-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, next, new UTF8Encoding(false));
            if (exists)
            {
                using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var bytes = new MemoryStream(); guard.CopyTo(bytes);
                if (!bytes.ToArray().SequenceEqual(before)) throw new IOException("A configuração mudou durante a preparação. Tente novamente.");
                File.Replace(temp, path, path + ".before-ddj-" + Guid.NewGuid().ToString("N"));
            }
            else File.Move(temp, path, overwrite: false);
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<int> Discover(int seconds)
    {
        if (seconds is < 5 or > 300) throw new ArgumentException("Discovery timeout must be 5..300 seconds");
        using var mutex = new Mutex(true, "Local\\DDJ200.CodexBridge.Engine", out bool created);
        if (!created) throw new InvalidOperationException("A ponte já está em execução. O assistente não interrompe uma sessão ativa.");
        var detected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new MicroUsbIp();
        server.LightingReceived += (method, value) =>
        {
            if (method != "v.oai.thstatus") return;
            Surface12Logic.ParseTasks(value); // Only a valid six-slot snapshot proves app feedback.
            detected.TrySetResult();
        };
        server.Start();
        Console.WriteLine("DISCOVERY_LISTENING");
        // No MIDI devices are opened and no input events can be emitted in this mode.
        try
        {
            await detected.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
            Console.WriteLine("MICRO_FEEDBACK_CONFIRMED");
            // Keep the device available until the UI has detached it and closes stdin.
            await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return 0;
        }
        catch (TimeoutException) { Console.Error.WriteLine("O aplicativo não confirmou o Micro. Abra o Codex e tente novamente; se a configuração acabou de ser criada, feche e reabra o Codex."); return 2; }
    }
}
