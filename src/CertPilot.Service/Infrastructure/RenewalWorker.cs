using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public sealed class RenewalWorker(IServiceScopeFactory scopes, PilotOptions options, RuntimeState runtime,
    MutationLock mutations, ILogger<RenewalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A single process owns the database (enforced by data-directory process lock in Program).
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PilotDb>();
            foreach (var job in await db.Jobs.Where(j => j.Status == "running").ToListAsync(stoppingToken))
            { job.Status = "queued"; job.Stage = "recovering"; }
            await db.SaveChangesAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            runtime.WorkerHeartbeatUtc = DateTimeOffset.UtcNow;
            try { await Tick(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError("Worker cycle failed: {Type}. See task status.", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.ScanSeconds, 1, 900)), stoppingToken);
        }
    }
    public async Task Tick(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PilotDb>();
        var engine = scope.ServiceProvider.GetRequiredService<CertificateEngine>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        // Resume durable cleanup for terminal tasks before allowing more DNS work.
        foreach (var pending in await db.Jobs.Where(j => j.Status == "failed" && j.CleanupJson != null && j.CleanupJson != "[]").ToListAsync(token))
        {
            try { await engine.Cleanup(pending); }
            catch (Exception) when (!token.IsCancellationRequested) { /* retain cleanup journal for next cycle */ }
        }
        await mutations.Gate.WaitAsync(token);
        WorkItem? selected = null;
        try
        {
            var certificates = await db.Certificates.ToListAsync(token);
            foreach (var c in certificates)
            {
                // Recover a committed pointer when process stopped between file and DB commit.
                var pointer = Path.Combine(c.OutputDirectory, c.FileName + ".current.json");
                if (File.Exists(pointer))
                {
                    var record = JsonSerializer.Deserialize<PublishedCertificate>(await File.ReadAllTextAsync(pointer, token));
                    if (record?.CertificateId == c.Id && record.Revision == c.Revision && record.Generation != c.CurrentGeneration)
                        CertificateEngine.ApplyPublished(c, record);
                }
                var active = await db.Jobs.AnyAsync(j => j.CertificateId == c.Id && (j.Status == "queued" || j.Status == "running"), token);
                if (!active && c.AutoRenew && c.NotAfterUtc is not null && c.RenewAtUtc <= DateTimeOffset.UtcNow)
                {
                    await queue.Enqueue(c.Id, "renewal", "scheduler", null);
                }
            }
            await db.SaveChangesAsync(token);
            selected = (await db.Jobs.Where(j => j.Status == "queued").ToListAsync(token))
                .Where(j => j.NextAttemptAtUtc <= DateTimeOffset.UtcNow).OrderBy(j => j.CreatedAtUtc).FirstOrDefault();
            if (selected is not null)
            { selected.Status = "running"; selected.Attempt++; selected.Stage = "starting"; await db.SaveChangesAsync(token); }
        }
        finally { mutations.Gate.Release(); }

        if (selected is not null)
        {
            var c = await db.Certificates.FindAsync([selected.CertificateId], token);
            if (c is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.AcmeTimeoutSeconds));
            try
            {
                if (selected.ConfigRevision != c.Revision) throw new PilotException(409, "REVISION_CONFLICT", "任务配置修订已变更。");
                if (selected.Kind == "inspection")
                {
                    var domain = JsonDocument.Parse(selected.ResultJson!).RootElement.GetProperty("domain").GetString()!;
                    selected.ResultJson = await SiteInspection.Run(c, domain, timeout.Token);
                }
                else
                {
                    // Recover successful publication rather than placing a duplicate order.
                    var publishedJob = c.CurrentGeneration is not null && Path.GetFileName(c.CurrentGeneration) == selected.Id;
                    if (!publishedJob) await engine.Issue(c, selected, timeout.Token);
                    else await engine.Cleanup(selected);
                    selected.ResultJson = JsonSerializer.Serialize(new { c.Thumbprint, c.NotAfterUtc, c.CurrentGeneration });
                }
                selected.Status = "succeeded"; selected.Stage = "complete"; selected.FinishedAtUtc = DateTimeOffset.UtcNow;
                selected.KeySecret = null; selected.Error = null; selected.ErrorCode = null;
                db.Events.Add(new() { Subject = "worker", Action = $"job.{selected.Kind}.succeeded", EntityId = c.Id });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { selected.Status = "queued"; selected.Stage = "interrupted"; }
            catch (Exception e)
            {
                selected.Status = "failed"; selected.FinishedAtUtc = DateTimeOffset.UtcNow;
                selected.ErrorCode = e is PilotException pe ? pe.Code : "ACME_OR_NETWORK_ERROR";
                selected.Error = e is PilotException safe ? safe.Message : "上游请求或证书处理失败；检查网络、CA 验证、DNS 凭据和路径权限。";
                if (selected.Kind != "inspection")
                {
                    c.FailureCount++; c.LatestError = selected.Error;
                    c.RenewAtUtc = DateTimeOffset.UtcNow + RenewalPolicy.RetryDelay(c.FailureCount) + TimeSpan.FromSeconds(Random.Shared.Next(15));
                    // Keep the same durable order/key for retry, including failed first issuance.
                    if (c.AutoRenew && selected.Attempt < 4)
                    {
                        selected.Status = "queued"; selected.Stage = "retrying"; selected.FinishedAtUtc = null;
                        selected.NextAttemptAtUtc = c.RenewAtUtc.Value;
                    }
                }
                logger.LogWarning("Job {Id} failed with {Code} ({Type})", selected.Id, selected.ErrorCode, e.GetType().Name);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        else
        {
            // One ARI refresh per idle tick; never polling the CA on every UI request.
            var c = (await db.Certificates.Where(c => c.AriId != null && c.LatestError == null).ToListAsync(token))
                .FirstOrDefault(c => c.NotAfterUtc > DateTimeOffset.UtcNow && c.AriRefreshAtUtc <= DateTimeOffset.UtcNow);
            if (c is not null) await engine.RefreshAri(c, token);
        }
        runtime.WorkerHeartbeatUtc = DateTimeOffset.UtcNow;
    }
}
