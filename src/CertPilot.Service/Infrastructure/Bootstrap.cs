using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public static class Bootstrap
{
    public static IServiceCollection AddPilotCore(this IServiceCollection services, PilotOptions options,
        RuntimeState runtime, MutationLock mutations)
    {
        services.AddSingleton(options);
        services.AddSingleton(runtime);
        services.AddSingleton(mutations);
        services.AddSingleton<SecretVault>();
        services.AddDbContext<PilotDb>(db => db.UseSqlite(
            $"Data Source={Path.Combine(options.DataDirectory, "certpilot.db")};Default Timeout=30"));
        services.AddIdentityCore<PilotUser>(o =>
        {
            o.Password.RequiredLength = 14;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<PilotDb>().AddSignInManager();
        var protection = services.AddDataProtection().SetApplicationName("CertPilot")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        services.AddHttpClient("external", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<CertificateEngine>();
        services.AddScoped<CertificateStore>();
        services.AddScoped<JobQueue>();
        return services;
    }

    public static async Task Initialize(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PilotDb>();
        // v0.1 has one initial schema; upgrades require an explicit migration in a future release.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Administrator", "Operator", "Viewer" })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PilotUser>>();
        if (await users.Users.AnyAsync()) return;
        var options = provider.GetRequiredService<PilotOptions>();
        var password = "Cp!" + SecretVault.NewToken();
        var user = new PilotUser { UserName = "admin", LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException("Administrator initialization failed.");
        await users.AddToRoleAsync(user, "Administrator");
        var file = Path.Combine(options.DataDirectory, "bootstrap-admin.txt");
        await File.WriteAllTextAsync(file, $"Username: admin{Environment.NewLine}Password: {password}{Environment.NewLine}Change on first login. This file is removed after password change.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static X509Certificate2 ManagementCertificate(PilotOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ManagementCertificatePath))
        {
            var password = options.ManagementCertificatePasswordFile is { Length: > 0 } file
                ? File.ReadAllText(file).TrimEnd('\r', '\n') : null;
            return X509CertificateLoader.LoadPkcs12FromFile(options.ManagementCertificatePath, password);
        }
        if (!IPAddress.IsLoopback(IPAddress.Parse(options.ListenAddress)))
            throw new InvalidOperationException("Remote binding requires an explicit trusted management certificate.");
        var path = Path.Combine(options.DataDirectory, "management.pfx");
        var vault = new SecretVault(options);
        var secretFile = Path.Combine(options.DataDirectory, "management.password");
        if (File.Exists(path) && File.Exists(secretFile))
            return X509CertificateLoader.LoadPkcs12FromFile(path, vault.Unprotect(File.ReadAllText(secretFile)));
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=CertPilot localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var passwordNew = SecretVault.NewToken();
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, passwordNew));
        File.WriteAllText(secretFile, vault.Protect(passwordNew));
        File.WriteAllBytes(Path.Combine(options.DataDirectory, "management.cer"), cert.Export(X509ContentType.Cert));
        if (!OperatingSystem.IsWindows())
            foreach (var f in new[] { path, secretFile }) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12FromFile(path, passwordNew);
    }
}
