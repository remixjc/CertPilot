using CertPilot;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
var options = builder.Configuration.GetSection("CertPilot").Get<PilotOptions>() ?? new();
options.DataDirectory = Path.GetFullPath(options.DataDirectory);
Directory.CreateDirectory(options.DataDirectory);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(options.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var runtime = new RuntimeState();
var mutations = new MutationLock();
builder.Services.AddWindowsService(o => o.ServiceName = "CertPilot");
builder.Services.AddPilotCore(options, runtime, mutations);
if (!options.DisableWorker) builder.Services.AddHostedService<RenewalWorker>();
builder.Services.AddHostedService<WebListener>();
using var host = builder.Build();
var passwordCommand = Array.IndexOf(args, "--pfx-password");
if (passwordCommand >= 0)
{
    if (passwordCommand + 1 >= args.Length) throw new ArgumentException("Use --pfx-password <certificate-id>.");
    if (OperatingSystem.IsWindows() && !new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Run from an elevated local terminal.");
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PilotDb>();
    var c = await db.Certificates.FindAsync(args[passwordCommand + 1]);
    if (c?.PfxPasswordSecret is null) throw new InvalidOperationException("No published PFX password exists for this certificate.");
    Console.WriteLine(scope.ServiceProvider.GetRequiredService<SecretVault>().Unprotect(c.PfxPasswordSecret));
    return;
}
using var processLock = new FileStream(Path.Combine(options.DataDirectory, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
await Bootstrap.Initialize(host.Services);
await host.RunAsync();
