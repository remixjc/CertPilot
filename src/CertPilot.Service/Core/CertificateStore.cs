using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public sealed class CertificateStore(PilotDb db, PilotOptions options)
{
    public static string[] Domains(CertificateConfig c) => JsonSerializer.Deserialize<string[]>(c.DomainsJson)!;
    public static object View(CertificateConfig c) => new
    {
        c.Id, c.Revision, c.Name, domains = Domains(c), c.Email, c.Environment,
        c.Validation, c.CredentialId, c.WebRoot, c.OutputDirectory, c.FileName, c.Format,
        c.AutoRenew, c.TermsAccepted, c.UpdatedAtUtc,
        certificate = new
        {
            status = c.NotAfterUtc is null ? "notIssued" : c.NotAfterUtc < DateTimeOffset.UtcNow ? "expired" : "valid",
            c.NotBeforeUtc, c.NotAfterUtc, c.Thumbprint, c.IssuedRevision,
            domains = c.IssuedDomainsJson is null ? [] : JsonSerializer.Deserialize<string[]>(c.IssuedDomainsJson),
            c.CurrentGeneration
        },
        renewal = new { status = !c.AutoRenew ? "paused" : c.LatestError is not null ? "failed" : "scheduled",
            nextAttemptAtUtc = c.RenewAtUtc, error = c.LatestError }
    };
    public async Task<CertificateConfig> Save(string? id, CertificateInput input, int? revision, string subject)
    {
        input = ConfigValidation.Validate(input, options);
        if (input.Validation == "dns" && !await db.Credentials.AnyAsync(c => c.Id == input.CredentialId))
            throw ConfigValidation.Invalid("DNS 凭据不存在。");
        var all = await db.Certificates.ToListAsync();
        if (all.Any(c => c.Id != id && string.Equals(Path.TrimEndingDirectorySeparator(c.OutputDirectory), input.OutputDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.FileName, input.FileName, StringComparison.OrdinalIgnoreCase)))
            throw new PilotException(409, "OUTPUT_CONFLICT", "保存目录和文件名与另一证书冲突。");
        var c = id is null ? new CertificateConfig() : all.Find(c => c.Id == id)
            ?? throw new PilotException(404, "NOT_FOUND", "证书不存在。");
        if (id is not null && c.Revision != revision) throw new PilotException(412, "REVISION_CONFLICT", "配置已被其他客户端修改，请刷新。");
        if (id is not null && await db.Jobs.AnyAsync(j => j.CertificateId == id && (j.Status == "queued" || j.Status == "running")))
            throw new PilotException(409, "JOB_ACTIVE", "请等待当前任务完成后修改。");
        var identityChanged = c.DomainsJson != JsonSerializer.Serialize(input.Domains) || c.Environment != input.Environment;
        if (c.CurrentGeneration is not null && (Path.TrimEndingDirectorySeparator(c.OutputDirectory) != input.OutputDirectory || c.FileName != input.FileName || c.Format != input.Format))
            throw new PilotException(409, "EXPORT_CHANGE_REQUIRES_NEW_CONFIG", "v0.1 已签发配置暂不支持改变输出位置/格式；请新建配置。");
        c.Name = input.Name; c.DomainsJson = JsonSerializer.Serialize(input.Domains); c.Email = input.Email;
        c.Environment = input.Environment; c.Validation = input.Validation; c.CredentialId = input.CredentialId;
        c.WebRoot = input.WebRoot; c.OutputDirectory = input.OutputDirectory; c.FileName = input.FileName;
        c.Format = input.Format; c.AutoRenew = input.AutoRenew;
        c.TermsAccepted = input.TermsAccepted;
        c.TermsAcceptedAtUtc = input.TermsAccepted ? c.TermsAcceptedAtUtc ?? DateTimeOffset.UtcNow : null;
        if (identityChanged) { c.AriId = null; c.AriRefreshAtUtc = null; c.RenewAtUtc = null; }
        if (id is null) db.Certificates.Add(c); else c.Revision++;
        c.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.Events.Add(new() { Subject = subject, Action = id is null ? "certificate.created" : "certificate.updated", EntityId = c.Id });
        await db.SaveChangesAsync();
        return c;
    }
}

public sealed class JobQueue(PilotDb db)
{
    public async Task<WorkItem> Enqueue(string id, string kind, string subject, string? key, string? domain = null)
    {
        if (kind is not ("issuance" or "renewal" or "inspection")) throw ConfigValidation.Invalid("未知任务。");
        var hash = SecretVault.Hash($"{id}:{kind}:{domain}");
        if (key is not null)
        {
            if (key.Length is < 8 or > 128) throw ConfigValidation.Invalid("Idempotency-Key 长度应为 8–128。");
            var existing = await db.Jobs.FirstOrDefaultAsync(j => j.Subject == subject && j.RequestKey == key);
            if (existing is not null)
            {
                if (existing.RequestHash != hash) throw new PilotException(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他请求。");
                return existing;
            }
        }
        var c = await db.Certificates.FindAsync(id) ?? throw new PilotException(404, "NOT_FOUND", "证书不存在。");
        if (await db.Jobs.AnyAsync(j => j.CertificateId == id && (j.Status == "queued" || j.Status == "running")))
            throw new PilotException(409, "JOB_ACTIVE", "此证书已有排队或运行中的任务。");
        if (kind != "inspection")
        {
            if (!c.TermsAccepted) throw ConfigValidation.Invalid("需先明确接受 Let's Encrypt 订阅协议。");
            if (c.RenewAtUtc > DateTimeOffset.UtcNow && c.LatestError is not null)
                throw new PilotException(429, "RETRY_BACKOFF", "失败退避尚未结束，请按下次重试时间操作。");
            if (kind == "renewal" && c.NotAfterUtc is null) throw ConfigValidation.Invalid("尚无证书，请先申请。");
            var recent = (await db.Jobs.Where(j => j.CertificateId == id && j.Kind != "inspection").ToListAsync())
                .Any(j => j.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
            if (recent) throw new PilotException(429, "RATE_LIMITED", "同一证书至少间隔一分钟发起签发。");
        }
        else
        {
            domain = ConfigValidation.NormalizeDomain(domain ?? CertificateStore.Domains(c).FirstOrDefault(d => !d.StartsWith("*.")) ?? "");
            if (domain.StartsWith("*.") || !CertificateStore.Domains(c).Contains(domain))
                throw ConfigValidation.Invalid("只能检测配置中的具体域名。");
        }
        var job = new WorkItem { CertificateId = id, ConfigRevision = c.Revision, Kind = kind,
            Subject = subject, RequestKey = key, RequestHash = hash, ResultJson = domain is null ? null : JsonSerializer.Serialize(new { domain }) };
        db.Jobs.Add(job);
        db.Events.Add(new() { Subject = subject, Action = $"job.{kind}.queued", EntityId = id });
        await db.SaveChangesAsync();
        return job;
    }
}
