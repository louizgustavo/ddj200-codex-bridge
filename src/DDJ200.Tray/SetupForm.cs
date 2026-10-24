using System.Diagnostics;

namespace DDJ200.CodexBridge;

internal sealed class SetupForm : Form
{
    private const string Endpoint="usbip://127.0.0.1:3240/1-1";
    private readonly string bridge,config,dataRoot;
    private readonly TextBox progress=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
    private readonly Button retry=new(){Text="Tentar novamente",AutoSize=true};
    private readonly Button close=new(){Text="Fechar",AutoSize=true};
    private bool running;
    private CancellationTokenSource? cancellation;
    public bool Confirmed {get;private set;}
    public SetupForm(string bridge,string config,string dataRoot,bool runAutomatically=true)
    {
        this.bridge=bridge;this.config=config;this.dataRoot=dataRoot;
        Text="Preparar DDJ-200 e Codex Micro";Width=670;Height=430;StartPosition=FormStartPosition.CenterScreen;
        var instructions=new Label{Text="Mantenha o Codex aberto. O assistente prepara somente configurações ausentes e verifica o retorno do Micro, sem executar comandos da controladora.",Dock=DockStyle.Top,Height=65,Padding=new Padding(12)};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=45,FlowDirection=FlowDirection.RightToLeft};
        buttons.Controls.Add(close);buttons.Controls.Add(retry);Controls.Add(progress);Controls.Add(instructions);Controls.Add(buttons);
        close.Click+=(_,_)=>{if(running)cancellation?.Cancel();else Close();};retry.Click+=async(_,_)=>await Run();
        if(runAutomatically)Shown+=async(_,_)=>await Run();
        FormClosing+=(_,e)=>{if(running){e.Cancel=true;cancellation?.Cancel();}};
    }
    private void Say(string text)=>progress.AppendText(text+Environment.NewLine+Environment.NewLine);
    private ProcessStartInfo Command(string command)
    {
        var info=new ProcessStartInfo(bridge){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        info.ArgumentList.Add(command);info.Environment["DDJ_CODEX_CONFIG"]=config;info.Environment["DDJ_BRIDGE_DATA"]=dataRoot;return info;
    }
    private async Task Run()
    {
        if(running)return;running=true;retry.Enabled=false;close.Text="Cancelar";progress.Clear();
        cancellation=new CancellationTokenSource();
        Process? discovery=null;bool ownsAttachment=false;
        string ownership=Path.Combine(dataRoot,"runtime","usbip-127.0.0.1-3240-1-1.owned");
        try
        {
            var ports=await UsbIpClient.Run("port");
            if(ports.Code!=0)throw new IOException("O driver USBip não está pronto. Reinicie o Windows se solicitado pelo instalador. "+ports.Text);
            if(ports.Text.Contains(Endpoint,StringComparison.OrdinalIgnoreCase)||File.Exists(ownership))
                throw new IOException("Já existe uma conexão da ponte ou limpeza pendente. Use Parar ponte antes de executar este assistente.");
            Say("USBip disponível. Verificando a configuração do Codex...");
            string codexFamily=await FindCodexPackage();
            using(var prepare=Process.Start(Command("setup-config"))??throw new IOException("Preparação indisponível."))
            {
                Task<string> output=prepare.StandardOutput.ReadToEndAsync(),error=prepare.StandardError.ReadToEndAsync();
                await prepare.WaitForExitAsync();string result=await output,detail=await error;
                if(prepare.ExitCode!=0)throw new IOException(detail);
                Say(result.Contains("CONFIG_PREPARED")?"Configuração inicial preparada; preferências existentes preservadas. Uma cópia de segurança foi criada se o arquivo já existia.":"Configuração compatível; recentes/fixados e suas ações foram preservados.");
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var launch=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=true};
            launch.ArgumentList.Add("shell:AppsFolder\\"+codexFamily+"!App");Process.Start(launch)?.Dispose();
            Say("Apresentando o Micro virtual. Abra o Codex se ele estiver fechado. A confirmação pode levar até dois minutos.");
            discovery=Process.Start(Command("discover-micro"))??throw new IOException("Não foi possível apresentar o Micro.");
            Task<string> errors=discovery.StandardError.ReadToEndAsync();
            string? first=await discovery.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10),cancellation.Token);
            if(first!="DISCOVERY_LISTENING")throw new IOException("O Micro não iniciou. "+await errors);
            Directory.CreateDirectory(Path.GetDirectoryName(ownership)!);
            File.WriteAllText(ownership,"Attached by DDJ-200 first-run setup");ownsAttachment=true;
            var attached=await UsbIpClient.Run("attach","-r","127.0.0.1","-b","1-1","--once");
            ports=await UsbIpClient.Run("port");
            if(attached.Code!=0||ports.Code!=0||!ports.Text.Contains(Endpoint,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Falha ao conectar o Micro virtual. "+attached.Text+ports.Text);
            string? feedback=await discovery.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(125),cancellation.Token);
            if(feedback!="MICRO_FEEDBACK_CONFIRMED")throw new IOException(await errors);
            Confirmed=true;
            Say("Codex Micro detectado: retorno real dos seis indicadores confirmado. Ao fechar, a ponte tentará conectar a DDJ-200 pelo transporte selecionado.");
        }
        catch(OperationCanceledException){Confirmed=false;Say("Preparação cancelada. Liberando somente a conexão criada por este assistente...");}
        catch(Exception e) when(e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Confirmed=false;Say("Preparação não concluída: "+e.Message+"\n\nSe o Codex já estava aberto quando a configuração foi criada, feche e reabra o aplicativo e tente novamente. Não é necessário pedir detecção pelo chat.");
        }
        finally
        {
            if(ownsAttachment)
            {
                try
                {
                    var ports=await UsbIpClient.Run("port");
                    if(ports.Code!=0)throw new IOException(ports.Text);
                    int? port=null;
                    foreach(string line in ports.Text.Split('\n'))
                    {
                        var match=System.Text.RegularExpressions.Regex.Match(line,@"^\s*Port\s+(\d+):");
                        if(match.Success)port=int.Parse(match.Groups[1].Value);
                        if(port.HasValue&&line.Trim().Equals("-> "+Endpoint,StringComparison.OrdinalIgnoreCase))
                        {var result=await UsbIpClient.Run("detach","-p",port.Value.ToString());if(result.Code!=0)throw new IOException(result.Text);break;}
                    }
                    ports=await UsbIpClient.Run("port");
                    if(ports.Code!=0||ports.Text.Contains(Endpoint,StringComparison.OrdinalIgnoreCase))throw new IOException("Micro ainda anexado.");
                    File.Delete(ownership);
                }
                catch(Exception e) {Confirmed=false;Say("A limpeza do USBip ficou pendente: "+e.Message+". Use Parar ponte antes de tentar novamente.");}
            }
            if(discovery!=null)
            {
                try {if(!discovery.HasExited){await discovery.StandardInput.WriteLineAsync("close");await discovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}}
                catch(Exception e) when(e is IOException or InvalidOperationException or TimeoutException){if(!discovery.HasExited)discovery.Kill();}
                discovery.Dispose();
            }
            running=false;retry.Enabled=true;close.Text="Fechar";cancellation.Dispose();cancellation=null;
        }
    }

    private static async Task<string> FindCodexPackage()
    {
        // Resolve the installed package of this user; never launch an arbitrary PATH executable.
        string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
        var info=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; (Get-AppxPackage -Name OpenAI.Codex).PackageFamilyName"})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Não foi possível localizar o aplicativo Codex.");
        Task<string> output=process.StandardOutput.ReadToEndAsync(),error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}
        catch(TimeoutException){if(!process.HasExited)process.Kill();throw;}
        string family=(await output).Trim();await error;
        if(process.ExitCode!=0||!System.Text.RegularExpressions.Regex.IsMatch(family,@"^OpenAI\.Codex_[A-Za-z0-9]+$"))
            throw new IOException("Instale e abra o aplicativo Codex/ChatGPT para Windows pela distribuição oficial da OpenAI, entre na sua conta e tente novamente. O pacote OpenAI.Codex não foi encontrado para este usuário.");
        return family;
    }
}
