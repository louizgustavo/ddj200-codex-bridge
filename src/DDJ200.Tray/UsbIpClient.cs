using System.Diagnostics;
using Microsoft.Win32;

namespace DDJ200.CodexBridge;

internal static class UsbIpClient
{
    public static string Executable
    {
        get
        {
            using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var install=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1");
            string dir=install?.GetValue("InstallLocation") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"USBip");
            return Path.Combine(dir,"usbip.exe");
        }
    }

    public static async Task<(int Code,string Text)> Run(params string[] args)
    {
        if(!File.Exists(Executable))throw new FileNotFoundException("USBip ausente. Execute novamente o instalador completo da ponte.");
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Não foi possível iniciar USBip.");
        Task<string> output=process.StandardOutput.ReadToEndAsync(),error=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch(TimeoutException) { if(!process.HasExited)process.Kill(); throw new TimeoutException("USBip não respondeu em 20 s. Reinicie o Windows se a instalação do driver estiver pendente."); }
        return(process.ExitCode,(await output)+(await error));
    }
}
