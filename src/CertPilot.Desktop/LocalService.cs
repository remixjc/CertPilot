using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace CertPilot.Desktop;

public static class LocalService
{
    public static string Status()
    {
        try { using var service = new ServiceController("CertPilot"); return service.Status.ToString(); }
        catch (InvalidOperationException) { return "NotInstalled"; }
    }
    public static Task Start() => Task.Run(() =>
    {
        using var service = new ServiceController("CertPilot");
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
    });
    public static Task Stop() => Task.Run(() =>
    {
        using var service = new ServiceController("CertPilot");
        if (service.Status != ServiceControllerStatus.Stopped) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    });
    public static async Task Install()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "install-service.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("安装脚本缺失，请完整解压 Windows 发布包后运行 CertPilot.exe。", script);
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException("安装服务失败：" + await stderr);
    }
}
