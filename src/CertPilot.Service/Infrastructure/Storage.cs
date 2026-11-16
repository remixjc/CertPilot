using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public sealed class PilotDb(DbContextOptions<PilotDb> options) : IdentityDbContext<PilotUser>(options)
{
    public DbSet<RequestReceipt> Receipts => Set<RequestReceipt>();
    public DbSet<CertificateConfig> Certificates => Set<CertificateConfig>();
    public DbSet<DnsCredential> Credentials => Set<DnsCredential>();
    public DbSet<WorkItem> Jobs => Set<WorkItem>();
    public DbSet<ProtectedValue> Secrets => Set<ProtectedValue>();
    public DbSet<ServiceToken> Tokens => Set<ServiceToken>();
    public DbSet<AuditEvent> Events => Set<AuditEvent>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<RequestReceipt>().HasIndex(r => new { r.Subject, r.Key }).IsUnique();
        builder.Entity<ServiceToken>().HasIndex(t => t.Hash).IsUnique();
        builder.Entity<WorkItem>().HasIndex(j => new { j.Subject, j.RequestKey }).IsUnique();
        builder.Entity<WorkItem>().HasIndex(j => j.CertificateId);
    }
}

public sealed class PilotOptions
{
    public string DataDirectory { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "CertPilot")
        : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CertPilot");
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8443;
    public bool DevelopmentHttp { get; set; }
    public string? ManagementCertificatePath { get; set; }
    public string? ManagementCertificatePasswordFile { get; set; }
    public string[] AllowedHosts { get; set; } = ["localhost", "127.0.0.1", "::1"];
    public string[] CorsOrigins { get; set; } = [];
    public string[] OutputRoots { get; set; } = [];
    public string[] WebRoots { get; set; } = [];
    public int ScanSeconds { get; set; } = 30;
    public int DnsPropagationSeconds { get; set; } = 60;
    public int AcmeTimeoutSeconds { get; set; } = 600;
    public bool DisableWorker { get; set; }
    public string EffectiveOutputRoot => Path.Combine(DataDirectory, "certificates");
}

public sealed class SecretVault(PilotOptions options)
{
    private readonly string keyPath = Path.Combine(options.DataDirectory, "development-encryption.key");
    public string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine));
        var key = GetDevelopmentKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, bytes, ciphertext, tag);
        return "aes:" + Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }
    public string Unprotect(string value)
    {
        if (value.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.LocalMachine));
        if (!value.StartsWith("aes:", StringComparison.Ordinal)) throw new CryptographicException("Unsupported secret format.");
        var bytes = Convert.FromBase64String(value[4..]);
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(GetDevelopmentKey(), 16);
        aes.Decrypt(bytes[..12], bytes[28..], bytes[12..28], plain);
        return Encoding.UTF8.GetString(plain);
    }
    private byte[] GetDevelopmentKey()
    {
        Directory.CreateDirectory(options.DataDirectory);
        if (!File.Exists(keyPath))
        {
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(RandomNumberGenerator.GetBytes(32));
        }
        return File.ReadAllBytes(keyPath);
    }
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class RuntimeState
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? WorkerHeartbeatUtc { get; set; }
    public string Web { get; set; } = "starting";
    public string? WebError { get; set; }
    public object Snapshot() => new {
        version = typeof(RuntimeState).Assembly.GetName().Version?.ToString(3),
        startedAtUtc = StartedAtUtc, web = new { status = Web, error = WebError },
        worker = new { status = WorkerHeartbeatUtc > DateTimeOffset.UtcNow.AddMinutes(-2) ? "running" : "starting",
            heartbeatUtc = WorkerHeartbeatUtc },
        platform = OperatingSystem.IsWindows() ? "windows-service" : "development"
    };
}
