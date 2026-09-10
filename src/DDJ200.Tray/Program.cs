using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DDJ200.CodexBridge;

internal static class Program
{
    private const string ShutdownEventName = "Local\\DDJ200.CodexBridge.Shutdown";
    private const string StopEventName = "Local\\DDJ200.CodexBridge.Stop";

    [STAThread]
    private static void Main()
    {
        if (Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--shutdown", StringComparison.OrdinalIgnoreCase)))
        {
            SignalExisting(ShutdownEventName);
            return;
        }
        if (Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--stop", StringComparison.OrdinalIgnoreCase)))
        {
            SignalExisting(StopEventName);
            return;
        }
        using var singleInstance = new Mutex(true, "Local\\DDJ200.CodexBridge.Tray", out bool created);
        if (!created) return;
        ApplicationConfiguration.Initialize();
        bool bluetoothSetup=Environment.GetCommandLineArgs().Skip(1).Any(a=>a.Equals("--bluetooth-setup",StringComparison.OrdinalIgnoreCase));
        Application.Run(new BridgeTrayContext(bluetoothSetup));
    }

    private static void SignalExisting(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    internal static string ShutdownSignal => ShutdownEventName;
    internal static string StopSignal => StopEventName;
}

internal sealed class BridgeTrayContext : ApplicationContext
{
    private const string ProductName = "DDJ-200 Codex Bridge";
    private const string RunValueName = "DDJ200CodexBridge";
    private readonly string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDJ200CodexBridge");
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem stateItem = new("Verificando...") { Enabled = false };
    private readonly ToolStripMenuItem startItem = new("Iniciar ponte");
    private readonly ToolStripMenuItem stopItem = new("Parar ponte");
    private readonly ToolStripMenuItem startupItem = new("Iniciar com o Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem connectionItem = new("Conexão: USB");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private readonly EventWaitHandle shutdownSignal = new(false, EventResetMode.AutoReset, Program.ShutdownSignal);
    private readonly EventWaitHandle stopSignal = new(false, EventResetMode.AutoReset, Program.StopSignal);
    private Process? bridgeProcess;
    private StreamWriter? stdout;
    private StreamWriter? stderr;
    private bool busy;
    private bool closing;
    private bool openBluetoothOnFirstTick;
    private string selectedTransport="usb";
    private ulong? selectedBleAddress;
    private string? selectedBleName;

    public BridgeTrayContext(bool bluetoothSetup=false)
    {
        openBluetoothOnFirstTick=bluetoothSetup;
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
        EnsureUserProfile();
        selectedTransport=LoadTransport();
        var menu = new ContextMenuStrip();
        menu.Items.Add(stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        connectionItem.DropDownItems.Add("Usar USB",null,async(_,_)=>await SelectUsbAsync());
        connectionItem.DropDownItems.Add("Conectar por Bluetooth...",null,async(_,_)=>await SelectBluetoothAsync());
        menu.Items.Add(connectionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add("Configurar controles e luzes...", null, async (_, _) => await ConfigureAsync());
        menu.Items.Add("Selecionar configuração do Codex...", null, async (_, _) => await SelectCodexConfigAsync());
        menu.Items.Add("Abrir pasta de diagnóstico", null, (_, _) => OpenFolder(dataRoot));
        menu.Items.Add("Documentação", null, (_, _) => OpenFile(Path.Combine(installRoot, "README.md")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, async (_, _) => await ExitAsync());

        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = ProductName,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += async (_, _) => await ToggleAsync();
        startItem.Click += async (_, _) => { if(selectedTransport=="bluetooth"&&!selectedBleAddress.HasValue)await SelectBluetoothAsync();else await StartAsync(showResult:true); };
        stopItem.Click += async (_, _) => await StopAsync(showResult: true);
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += (_, _) => SetStartup(startupItem.Checked);
        timer.Tick += async (_, _) =>
        {
            if(openBluetoothOnFirstTick){openBluetoothOnFirstTick=false;await SelectBluetoothAsync();return;}
            if (shutdownSignal.WaitOne(0)) { await ExitAsync(); return; }
            if (stopSignal.WaitOne(0)) await StopAsync(showResult: false);
            RefreshStatus();
        };
        timer.Start();
        RefreshStatus();
        if(!bluetoothSetup&&selectedTransport=="usb")_ = StartAsync(showResult: false);
    }

    private string RuntimeRoot => Path.Combine(dataRoot, "runtime");
    private string StatusPath => Path.Combine(RuntimeRoot, "surface12", "status.json");
    private string StopPath => Path.Combine(RuntimeRoot, "surface12", "stop");
    private string UsbIpOwnershipPath => Path.Combine(RuntimeRoot, "usbip-127.0.0.1-3240-1-1.owned");
    private string ConfigPath => Path.Combine(dataRoot, "settings.json");
    private string UserProfilePath => Path.Combine(dataRoot, "controller-profile.json");
    private string DefaultProfilePath => Path.Combine(installRoot, "config", "controller-profile.json");
    private string BridgeExe => Path.Combine(installRoot, "bridge", "ddj200.exe");
    private static string UsbIpExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");

    private async Task ToggleAsync()
    {
        if (ReadLiveStatus().Live) await StopAsync(showResult: true);
        else if(selectedTransport=="bluetooth"&&!selectedBleAddress.HasValue)await SelectBluetoothAsync();else await StartAsync(showResult: true);
    }

    private async Task<bool> StartAsync(bool showResult)
    {
        if (busy || closing) return false;
        if (ReadLiveStatus().Live) return true;
        busy = true;
        RefreshStatus("Iniciando...");
        try
        {
            CloseLogs();
            if (!File.Exists(BridgeExe)) throw new FileNotFoundException("Executável da ponte não encontrado.", BridgeExe);
            if (!File.Exists(UsbIpExe)) throw new FileNotFoundException("USBip não está instalado. Use somente o instalador oficial indicado na documentação.", UsbIpExe);
            string codexConfig = ResolveCodexConfig();
            if (!File.Exists(codexConfig)) throw new FileNotFoundException("Selecione o arquivo config.toml usado pelo Codex.", codexConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
            if (File.Exists(StopPath)) File.Delete(StopPath);

            stdout = NewLog("bridge.stdout.log");
            stderr = NewLog("bridge.stderr.log");
            var info = new ProcessStartInfo(BridgeExe)
            {
                WorkingDirectory = dataRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in new[] { "surface12-run", "--continuous", "--arm", "--allow-task-selection", "--enable-commands", "ACT06,ACT07,ACT08,ACT09,ACT10_ACT11,ACT12", "--observe-analogs", "--execute-analogs" }) info.ArgumentList.Add(arg);
            info.ArgumentList.Add("--transport");info.ArgumentList.Add(selectedTransport);
            if(selectedTransport=="bluetooth")
            {
                if(!selectedBleAddress.HasValue)throw new InvalidOperationException("Escolha a DDJ-200 na tela Bluetooth antes de iniciar.");
                info.ArgumentList.Add("--ble-address");info.ArgumentList.Add(selectedBleAddress.Value.ToString("X"));
                info.ArgumentList.Add("--ble-name");info.ArgumentList.Add(selectedBleName??"DDJ-200");
            }
            info.Environment["DDJ_CODEX_CONFIG"] = codexConfig;
            info.Environment["DDJ_BRIDGE_DATA"] = dataRoot;
            info.Environment["DDJ_BRIDGE_PROFILE"] = UserProfilePath;
            bridgeProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
            bridgeProcess.OutputDataReceived += (_, e) => { if (e.Data != null) WriteLog(stdout, e.Data); };
            bridgeProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) WriteLog(stderr, e.Data); };
            if (!bridgeProcess.Start()) throw new InvalidOperationException("O Windows recusou iniciar a ponte.");
            bridgeProcess.BeginOutputReadLine();
            bridgeProcess.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            bool attachTried = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150);
                var current = ReadLiveStatus();
                if (bridgeProcess.HasExited) throw new InvalidOperationException(current.Detail ?? "A ponte encerrou durante a inicialização. Consulte o diagnóstico.");
                if (current.State == "active") { RefreshStatus(); if (showResult) Balloon("Ponte ativa", ToolTipIcon.Info); return true; }
                if (current.State == "error") throw new InvalidOperationException(current.Detail ?? "A ponte informou um erro.");
                if (current.State == "waiting_for_micro" && !attachTried)
                {
                    await AttachUsbIpAsync();
                    attachTried = true;
                }
            }
            throw new TimeoutException("O Codex Micro não confirmou a conexão em 30 segundos.");
        }
        catch (Exception ex)
        {
            string? cleanupError = await StopAndDetachAsync();
            RefreshStatus("Erro de inicialização");
            string message = cleanupError == null ? ex.Message : $"{ex.Message}\n\nA limpeza também falhou: {cleanupError}";
            if (showResult) MessageBox.Show(message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Balloon(message, ToolTipIcon.Warning);
            return false;
        }
        finally { busy = false; RefreshStatus(); }
    }

    private void EnsureUserProfile()
    {
        if (File.Exists(UserProfilePath)) return;
        if (!File.Exists(DefaultProfilePath)) throw new FileNotFoundException("Perfil padrão não encontrado.",DefaultProfilePath);
        File.Copy(DefaultProfilePath,UserProfilePath,false);
    }

    private async Task SelectUsbAsync()
    {
        if(selectedTransport=="usb"&&ReadLiveStatus().Live)return;
        if(ReadLiveStatus().Live&&MessageBox.Show("A conexão atual será encerrada antes de usar USB. Continuar?",ProductName,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
        string? error=await StopAndDetachAsync();if(error!=null||ReadLiveStatus().Live){MessageBox.Show(error??"A conexão atual ainda está ativa.",ProductName,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
        selectedTransport="usb";selectedBleAddress=null;selectedBleName=null;SaveTransport();RefreshStatus("Iniciando USB...");await StartAsync(showResult:true);
    }

    private async Task SelectBluetoothAsync()
    {
        if(ReadLiveStatus().Live&&MessageBox.Show("A conexão USB será encerrada antes de procurar Bluetooth. Continuar?",ProductName,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
        string? error=await StopAndDetachAsync();if(error!=null||ReadLiveStatus().Live){MessageBox.Show(error??"A conexão atual ainda está ativa.",ProductName,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
        using var dialog=new BluetoothConnectionForm();
        if(dialog.ShowDialog()!=DialogResult.OK||!dialog.SelectedAddress.HasValue){RefreshStatus("Desconectado");return;}
        selectedTransport="bluetooth";selectedBleAddress=dialog.SelectedAddress;selectedBleName=dialog.SelectedName;SaveTransport();RefreshStatus("Conectando Bluetooth...");await StartAsync(showResult:true);
    }

    private async Task ConfigureAsync()
    {
        using var form=new SettingsForm(UserProfilePath,DefaultProfilePath,IdentifyControlAsync);
        if(form.ShowDialog()!=DialogResult.OK||string.IsNullOrWhiteSpace(form.SerializedProfile))return;
        try{await ValidateProfileAsync(form.SerializedProfile);}catch(Exception ex){MessageBox.Show("A configuração não foi aplicada: "+ex.Message,ProductName,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
        bool wasLive=ReadLiveStatus().Live;
        if(wasLive&&MessageBox.Show("Para aplicar com segurança, a ponte será parada e reiniciada. Continuar?",ProductName,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
        if(wasLive)
        {
            string? cleanupError=await StopAndDetachAsync();
            if(cleanupError!=null||ReadLiveStatus().Live){MessageBox.Show(cleanupError??"A ponte ainda está ativa.",ProductName,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
        }
        string temp=UserProfilePath+".new"; string backup=UserProfilePath+".previous";
        await File.WriteAllTextAsync(temp,form.SerializedProfile);
        File.Replace(temp,UserProfilePath,backup,true);
        if(wasLive&&!await StartAsync(showResult:true))
        {
            string rollback=UserProfilePath+".rollback";
            File.Copy(backup,rollback,true);File.Move(rollback,UserProfilePath,true);
            bool restored=await StartAsync(showResult:false);
            MessageBox.Show(restored?"A nova configuração não iniciou. O perfil anterior foi restaurado e está ativo.":"A nova configuração não iniciou. O perfil anterior foi restaurado, mas a ponte continua inativa; consulte o diagnóstico.",ProductName,MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        else if(!wasLive)Balloon("Configuração salva. Ela será usada na próxima inicialização.",ToolTipIcon.Info);
    }

    private async Task ValidateProfileAsync(string serialized)
    {
        string candidate=Path.Combine(dataRoot,"profile-candidate-"+Guid.NewGuid().ToString("N")+".json");
        await File.WriteAllTextAsync(candidate,serialized);
        try
        {
            var info=new ProcessStartInfo(BridgeExe){WorkingDirectory=dataRoot,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add("profile-check");info.Environment["DDJ_BRIDGE_PROFILE"]=candidate;info.Environment["DDJ_BRIDGE_DATA"]=dataRoot;info.Environment["DDJ_CODEX_CONFIG"]=ResolveCodexConfig();
            using var process=Process.Start(info)??throw new InvalidOperationException("Validador indisponível.");
            Task<string> error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();string detail=await error;
            if(process.ExitCode!=0)throw new InvalidDataException(string.IsNullOrWhiteSpace(detail)?"perfil recusado pelo validador seguro.":detail.Trim());
        }
        finally{try{File.Delete(candidate);}catch(IOException){}}
    }

    private async Task<(string Control,string Signature)?> IdentifyControlAsync()
    {
        bool wasLive=ReadLiveStatus().Live;
        if(wasLive&&MessageBox.Show("Para aprender sem executar comandos, a ponte será pausada por alguns segundos. Continuar?",ProductName,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return null;
        if(wasLive)
        {
            string? cleanupError=await StopAndDetachAsync();
            if(cleanupError!=null||ReadLiveStatus().Live)throw new InvalidOperationException(cleanupError??"A ponte ainda está ativa.");
        }
        try
        {
            var info=new ProcessStartInfo(BridgeExe){WorkingDirectory=dataRoot,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add("identify-control");info.ArgumentList.Add("--timeout-seconds");info.ArgumentList.Add("20");
            info.ArgumentList.Add("--transport");info.ArgumentList.Add(selectedTransport);
            if(selectedTransport=="bluetooth")
            {
                if(!selectedBleAddress.HasValue)throw new InvalidOperationException("Conecte a DDJ-200 por Bluetooth antes de aprender um controle.");
                info.ArgumentList.Add("--ble-address");info.ArgumentList.Add(selectedBleAddress.Value.ToString("X"));
                info.ArgumentList.Add("--ble-name");info.ArgumentList.Add(selectedBleName??"DDJ-200");
            }
            info.Environment["DDJ_BRIDGE_DATA"]=dataRoot;info.Environment["DDJ_BRIDGE_PROFILE"]=UserProfilePath;
            using var process=Process.Start(info)??throw new InvalidOperationException("Não foi possível iniciar a aprendizagem.");
            Task<string> output=process.StandardOutput.ReadToEndAsync();Task<string> error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();string json=await output;string detail=await error;
            if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)?"Nenhum controle foi reconhecido.":detail.Trim());
            using var document=JsonDocument.Parse(json);
            return(document.RootElement.GetProperty("control").GetString()!,document.RootElement.GetProperty("signature").GetString()!);
        }
        finally{if(wasLive)await StartAsync(showResult:false);}
    }

    private async Task StopAsync(bool showResult)
    {
        if (busy) return;
        busy = true;
        RefreshStatus("Parando...");
        try
        {
            string? cleanupError = await StopAndDetachAsync();
            if (cleanupError != null) throw new InvalidOperationException(cleanupError);
            if (showResult) Balloon("Ponte parada e USB/IP liberado", ToolTipIcon.Info);
        }
        catch (Exception ex) { if (showResult) MessageBox.Show(ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; RefreshStatus(); }
    }

    private async Task RequestCleanStopAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StopPath)!);
        await File.WriteAllTextAsync(StopPath, "Clean stop requested by tray application");
        var live = ReadLiveStatus();
        Process? process = bridgeProcess;
        if ((process == null || process.HasExited) && live.Pid is > 0)
        {
            try { process = Process.GetProcessById(live.Pid.Value); } catch (ArgumentException) { process = null; }
        }
        if (process != null && !process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("A ponte ainda está encerrando; nenhum processo foi forçado."); }
        }
        CloseLogs();
    }

    private async Task AttachUsbIpAsync()
    {
        string ports = await RunUsbIpAsync(true, "port");
        if (ports.Contains("usbip://127.0.0.1:3240/1-1", StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(UsbIpOwnershipPath)!);
        await File.WriteAllTextAsync(UsbIpOwnershipPath, "Attached by DDJ-200 Codex Bridge");
        string result = await RunUsbIpAsync(true, "attach", "-r", "127.0.0.1", "-b", "1-1", "--once");
        ports = await RunUsbIpAsync(true, "port");
        if (!ports.Contains("usbip://127.0.0.1:3240/1-1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Falha ao conectar o dispositivo virtual USB/IP. " + result.Trim());
    }

    private async Task DetachUsbIpAsync()
    {
        if (!File.Exists(UsbIpOwnershipPath)) return;
        string ports = await RunUsbIpAsync(true, "port");
        int? port = null;
        foreach (string line in ports.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*Port\s+(\d+):");
            if (match.Success) port = int.Parse(match.Groups[1].Value);
            if (port.HasValue && line.Trim().Equals("-> usbip://127.0.0.1:3240/1-1", StringComparison.OrdinalIgnoreCase))
            {
                await RunUsbIpAsync(false, "detach", "-p", port.Value.ToString());
                string after = await RunUsbIpAsync(true, "port");
                if (after.Contains("usbip://127.0.0.1:3240/1-1", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("USB/IP ainda aparece anexado após a solicitação de detach.");
                File.Delete(UsbIpOwnershipPath);
                return;
            }
        }
        File.Delete(UsbIpOwnershipPath);
    }

    private async Task<string?> StopAndDetachAsync()
    {
        var errors = new List<string>();
        try { await RequestCleanStopAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        try { await DetachUsbIpAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        return errors.Count == 0 ? null : string.Join(" ", errors.Distinct());
    }

    private static async Task<string> RunUsbIpAsync(bool allowFailure, params string[] args)
    {
        var info = new ProcessStartInfo(UsbIpExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível executar USBip.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"USBip falhou ({process.ExitCode}): {error}");
        return output + error;
    }

    private (bool Live, int? Pid, string? State, string? Detail) ReadLiveStatus()
    {
        try
        {
            if (!File.Exists(StatusPath)) return (false, null, null, null);
            using var document = JsonDocument.Parse(File.ReadAllText(StatusPath));
            var root = document.RootElement;
            int pid = root.GetProperty("pid").GetInt32();
            string? state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
            string? detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            DateTimeOffset time = root.GetProperty("time").GetDateTimeOffset();
            bool processLive;
            try { using var process = Process.GetProcessById(pid); processLive = !process.HasExited && process.ProcessName.Equals("ddj200", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { processLive = false; }
            if ((DateTimeOffset.UtcNow - time).TotalSeconds > 8) state = "sem status recente";
            return (processLive, pid, state, detail);
        }
        catch (IOException) { return (false, null, "status indisponível", null); }
        catch (JsonException) { return (false, null, "status inválido", null); }
    }

    private void RefreshStatus(string? forced = null)
    {
        if (tray == null) return;
        var status = ReadLiveStatus();
        string label = forced ?? (status.Live ? status.State == "active" ? "Ativa" : "Conectando" : "Parada");
        stateItem.Text = "Estado: " + label;
        startItem.Enabled = !busy && !status.Live;
        stopItem.Enabled = !busy && status.Live;
        connectionItem.Text="Conexão: "+(selectedTransport=="usb"?"USB":"Bluetooth");
        tray.Text = (ProductName + " — " + label)[..Math.Min(63, ProductName.Length + 3 + label.Length)];
    }

    private string ResolveCodexConfig()
    {
        if (File.Exists(ConfigPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (document.RootElement.TryGetProperty("codexConfigPath", out var p) && !string.IsNullOrWhiteSpace(p.GetString())) return p.GetString()!;
        }
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        if (File.Exists(standard)) { SaveCodexConfig(standard); return standard; }
        return standard;
    }

    private async Task SelectCodexConfigAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Configuração TOML|*.toml|Todos os arquivos|*.*", Title = "Selecione config.toml do Codex", CheckFileExists = true };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        SaveCodexConfig(dialog.FileName);
        if (!ReadLiveStatus().Live) await StartAsync(showResult: true);
    }

    private void SaveCodexConfig(string path)
    {
        var settings=ReadSettings();settings["codexConfigPath"]=path;settings["transport"]=selectedTransport;AtomicSaveSettings(settings);
    }
    private string LoadTransport()
    {
        string? value=ReadSettings()["transport"]?.GetValue<string>();return value is "usb" or "bluetooth"?value:"usb";
    }
    private void SaveTransport(){var settings=ReadSettings();settings["transport"]=selectedTransport;AtomicSaveSettings(settings);}
    private JsonObject ReadSettings()
    {
        try{return File.Exists(ConfigPath)?JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject()??new JsonObject():new JsonObject();}
        catch(JsonException){return new JsonObject();}
    }
    private void AtomicSaveSettings(JsonObject settings)
    {
        string temp=ConfigPath+".new";File.WriteAllText(temp,settings.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,ConfigPath,true);
    }
    private StreamWriter NewLog(string name) => new(Path.Combine(dataRoot, "logs", name), append: true, Encoding.UTF8) { AutoFlush = true };
    private static void WriteLog(StreamWriter? writer, string line) { try { writer?.WriteLine($"{DateTimeOffset.Now:O} {line}"); } catch (Exception e) when (e is IOException or ObjectDisposedException) { } }
    private void CloseLogs() { stdout?.Dispose(); stderr?.Dispose(); stdout = stderr = null; bridgeProcess?.Dispose(); bridgeProcess = null; }
    private void Balloon(string message, ToolTipIcon icon) { tray.BalloonTipTitle = ProductName; tray.BalloonTipText = message; tray.BalloonTipIcon = icon; tray.ShowBalloonTip(4000); }
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    private static void OpenFile(string path) { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(RunValueName) is string value && value.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\""); else key.DeleteValue(RunValueName, false);
        startupItem.Checked = IsStartupEnabled();
    }

    private async Task ExitAsync()
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        string? cleanupError = await StopAndDetachAsync();
        if (cleanupError != null || ReadLiveStatus().Live || File.Exists(UsbIpOwnershipPath))
        {
            closing = false;
            timer.Start();
            RefreshStatus("Não foi possível encerrar com segurança");
            MessageBox.Show(cleanupError ?? "A ponte ou o USB/IP ainda está ativo; nenhum processo foi forçado.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        tray.Visible = false;
        tray.Dispose();
        shutdownSignal.Dispose();
        stopSignal.Dispose();
        ExitThread();
    }
}
