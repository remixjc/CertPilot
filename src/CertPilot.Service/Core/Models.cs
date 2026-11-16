using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace CertPilot;

public sealed class PilotUser : IdentityUser
{
    public bool MustChangePassword { get; set; } = true;
}

public sealed class CertificateConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ConcurrencyCheck] public int Revision { get; set; } = 1;
    public string Name { get; set; } = "";
    public string DomainsJson { get; set; } = "[]";
    public string Email { get; set; } = "";
    public string Environment { get; set; } = "staging";
    public string Validation { get; set; } = "http";
    public string? CredentialId { get; set; }
    public string? WebRoot { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "both";
    public bool AutoRenew { get; set; } = true;
    public bool TermsAccepted { get; set; }
    public DateTimeOffset? TermsAcceptedAtUtc { get; set; }
    public string? Thumbprint { get; set; }
    public string? IssuedDomainsJson { get; set; }
    public int? IssuedRevision { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? NotAfterUtc { get; set; }
    public DateTimeOffset? RenewAtUtc { get; set; }
    public DateTimeOffset? AriRefreshAtUtc { get; set; }
    public string? AriId { get; set; }
    public string? LatestError { get; set; }
    public int FailureCount { get; set; }
    public string? CurrentGeneration { get; set; }
    public string? PfxPasswordSecret { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DnsCredential
{
    public int Revision { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "cloudflare";
    public string ZoneId { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public string EncryptedToken { get; set; } = "";
}

public sealed class WorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CertificateId { get; set; } = "";
    public int ConfigRevision { get; set; }
    public string Kind { get; set; } = "issuance";
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
    public string? Subject { get; set; }
    public string? RequestKey { get; set; }
    public string? RequestHash { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? OrderUrl { get; set; }
    public string? KeySecret { get; set; }
    public string? CleanupJson { get; set; }
}

public sealed class ProtectedValue
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ServiceToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Scopes { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool Revoked { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Subject { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityId { get; set; } = "";
}

public sealed record CertificateInput(
    string Name, string[] Domains, string Email, string Environment, string Validation,
    string? CredentialId, string? WebRoot, string OutputDirectory, string FileName,
    string Format, bool AutoRenew, bool TermsAccepted);
public sealed record CredentialInput(string Name, string ZoneId, string ZoneName, string Token);
public sealed record LoginInput(string Username, string Password);
public sealed record PasswordInput(string CurrentPassword, string NewPassword);
public sealed record TokenInput(string Name, string[] Scopes, int Days = 30);
public sealed record UserInput(string Username, string Password, string Role);
public sealed record JobRequest(string? Domain);

public sealed class PilotException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class MutationLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public static class RenewalPolicy
{
    public static DateTimeOffset Fallback(DateTimeOffset start, DateTimeOffset end)
    {
        var lifetime = end - start;
        if (lifetime <= TimeSpan.Zero) throw new ArgumentException("Invalid certificate lifetime.");
        return start + lifetime * (lifetime.TotalDays < 10 ? .5 : 2d / 3);
    }
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMinutes(attempt switch { <= 1 => 1, 2 => 10, 3 => 100, _ => 1440 });
}

public sealed class RequestReceipt
{
    public long Id { get; set; }
    public string Subject { get; set; } = "";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string EntityId { get; set; } = "";
}
