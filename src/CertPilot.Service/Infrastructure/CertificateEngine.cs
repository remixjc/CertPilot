using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public sealed record ChallengeCleanup(string Type, string Target, string? CredentialId);
public sealed record PublishedCertificate(string CertificateId, string JobId, int Revision, string Generation,
    string Thumbprint, string[] Domains, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, string AriId);

public sealed class CertificateEngine(PilotDb db, SecretVault vault, PilotOptions options, IHttpClientFactory clients)
{
    private static Uri DirectoryFor(string environment) => environment == "production"
        ? WellKnownServers.LetsEncryptV2 : WellKnownServers.LetsEncryptStagingV2;

    private async Task<AcmeContext> Context(CertificateConfig c)
    {
        var id = $"acme:{c.Environment}:{SecretVault.Hash(c.Email.ToLowerInvariant())}";
        var saved = await db.Secrets.FindAsync(id);
        var key = saved is null ? KeyFactory.NewKey(KeyAlgorithm.ES256) : KeyFactory.FromPem(vault.Unprotect(saved.Value));
        // Persist the key before network IO so retrying account registration uses the same identity.
        if (saved is null)
        {
            db.Secrets.Add(new() { Id = id, Value = vault.Protect(key.ToPem()) });
            await db.SaveChangesAsync();
        }
        var context = new AcmeContext(DirectoryFor(c.Environment), key);
        await context.NewAccount(c.Email, termsOfServiceAgreed: c.TermsAccepted);
        return context;
    }

    public async Task Issue(CertificateConfig c, WorkItem job, CancellationToken cancellation)
    {
        if (!c.TermsAccepted) throw ConfigValidation.Invalid("未接受 CA 协议。");
        await Cleanup(job);
        var context = await Context(c);
        var domains = CertificateStore.Domains(c);
        IOrderContext order;
        if (job.OrderUrl is not null)
        {
            order = context.Order(new Uri(job.OrderUrl));
            if ((await order.Resource()).Status is OrderStatus.Invalid)
            {
                job.OrderUrl = null; job.KeySecret = null; await db.SaveChangesAsync();
                order = await context.NewOrder(domains);
                job.OrderUrl = order.Location.ToString(); await db.SaveChangesAsync();
            }
        }
        else
        {
            order = c.AriId is { Length: > 0 }
                ? await context.NewReplacementOrder(domains, c.AriId)
                : await context.NewOrder(domains);
            job.OrderUrl = order.Location.ToString();
            await db.SaveChangesAsync();
        }
        job.KeySecret ??= vault.Protect(KeyFactory.NewKey(KeyAlgorithm.RS256).ToPem());
        job.Stage = "authorizing"; await db.SaveChangesAsync();
        var key = KeyFactory.FromPem(vault.Unprotect(job.KeySecret));
        try
        {
            foreach (var authorization in await order.Authorizations())
            {
                cancellation.ThrowIfCancellationRequested();
                var auth = await authorization.Resource();
                if (auth.Status == AuthorizationStatus.Valid) continue;
                if (auth.Status != AuthorizationStatus.Pending)
                    throw new PilotException(422, "ACME_AUTHORIZATION_INVALID", "域名授权已失败或过期。");
                var challenge = c.Validation == "dns" ? await authorization.Dns() : await authorization.Http();
                if (c.Validation == "dns")
                {
                    var credential = await db.Credentials.FindAsync(c.CredentialId)
                        ?? throw ConfigValidation.Invalid("DNS 凭据已不存在。");
                    var name = "_acme-challenge." + auth.Identifier.Value.Replace("*.", "");
                    if (!(name.EndsWith("." + credential.ZoneName, StringComparison.OrdinalIgnoreCase)))
                        throw ConfigValidation.Invalid("域名不在 DNS 凭据指定 Zone 中。");
                    var txt = context.AccountKey.DnsTxt(challenge.Token);
                    var recordId = await CreateTxt(credential, name, txt, cancellation);
                    await Track(job, new("dns", recordId, credential.Id));
                    await Task.Delay(TimeSpan.FromSeconds(options.DnsPropagationSeconds), cancellation);
                }
                else
                {
                    var root = ConfigValidation.SafePath(c.WebRoot ?? "", options.WebRoots);
                    var dir = ConfigValidation.SafePath(Path.Combine(root, ".well-known", "acme-challenge"), options.WebRoots);
                    System.IO.Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, challenge.Token);
                    // CreateNew prevents overwriting an existing site's challenge.
                    await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    await using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(challenge.KeyAuthz);
                    await Track(job, new("http", file, null));
                }
                await challenge.Validate();
                var valid = false;
                for (var i = 0; i < 120; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
                    auth = await authorization.Resource();
                    if (auth.Status == AuthorizationStatus.Valid) { valid = true; break; }
                    if (auth.Status != AuthorizationStatus.Pending)
                        throw new PilotException(422, "ACME_VALIDATION_FAILED", "CA 验证域名失败；检查 DNS、CAA 和公网验证路径。");
                }
                if (!valid) throw new PilotException(504, "CHALLENGE_TIMEOUT", "CA 验证等待超时。");
            }
            job.Stage = "finalizing"; await db.SaveChangesAsync();
            CertificateChain chain;
            var resource = await order.Resource();
            if (resource.Status == OrderStatus.Valid) chain = await order.Download();
            else
            {
                if (resource.Status == OrderStatus.Ready) await order.Finalize(new CsrInfo(), key);
                var ready = false;
                for (var i = 0; i < 120; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    resource = await order.Resource();
                    if (resource.Status == OrderStatus.Valid) { ready = true; break; }
                    if (resource.Status == OrderStatus.Invalid) throw new PilotException(422, "ACME_ORDER_INVALID", "CA 拒绝签发。");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
                }
                if (!ready) throw new PilotException(504, "ORDER_TIMEOUT", "证书签发等待超时。");
                chain = await order.Download();
            }
            await Publish(c, job, chain, key, cancellation);
        }
        finally { await Cleanup(job); }
    }

    private async Task Publish(CertificateConfig c, WorkItem job, CertificateChain chain, IKey key, CancellationToken token)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(chain.Certificate.ToDer());
        var actualDomains = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(x => x.EnumerateDnsNames()).Select(x => x.ToLowerInvariant()).Order().ToArray();
        var expected = CertificateStore.Domains(c).Order().ToArray();
        if (!actualDomains.SequenceEqual(expected) || leaf.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new PilotException(422, "CERTIFICATE_MISMATCH", "下载的证书域名或有效期与订单不符。");
        var password = c.PfxPasswordSecret is null ? SecretVault.NewToken() : vault.Unprotect(c.PfxPasswordSecret);
        var pfx = chain.ToPfx(key).Build(c.Name, password);
        using var loaded = X509CertificateLoader.LoadPkcs12(pfx, password);
        if (!loaded.HasPrivateKey || loaded.Thumbprint != leaf.Thumbprint)
            throw new PilotException(422, "PRIVATE_KEY_MISMATCH", "证书与私钥不匹配。");
        var root = ConfigValidation.SafePath(c.OutputDirectory, [options.EffectiveOutputRoot, .. options.OutputRoots]);
        var generation = Path.Combine(root, ".certpilot", c.Id, job.Id);
        ConfigValidation.SafePath(generation, [options.EffectiveOutputRoot, .. options.OutputRoots]);
        System.IO.Directory.CreateDirectory(generation);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(generation, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var prefix = Path.Combine(generation, c.FileName);
        if (c.Format != "pfx")
        {
            await File.WriteAllTextAsync(prefix + ".crt", leaf.ExportCertificatePem(), token);
            await File.WriteAllTextAsync(prefix + ".fullchain.pem", chain.ToPem(), token);
            await File.WriteAllTextAsync(prefix + ".key", key.ToPem(), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(prefix + ".key", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        if (c.Format != "pem")
        {
            await File.WriteAllBytesAsync(prefix + ".pfx", pfx, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(prefix + ".pfx", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var record = new PublishedCertificate(c.Id, job.Id, c.Revision, generation, leaf.Thumbprint, actualDomains,
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            chain.GetRenewalInfoCertificateId());
        c.PfxPasswordSecret = vault.Protect(password);
        // Commit secret before publishing the pointer: crash recovery can still open this generation.
        await db.SaveChangesAsync(token);
        var manifest = Path.Combine(root, c.FileName + ".current.json");
        var temp = manifest + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), token);
        File.Move(temp, manifest, overwrite: true); // Single atomic pointer switch on the same volume.
        ApplyPublished(c, record);
        await db.SaveChangesAsync(token);
    }

    public static void ApplyPublished(CertificateConfig c, PublishedCertificate record)
    {
        c.CurrentGeneration = record.Generation; c.Thumbprint = record.Thumbprint;
        c.NotBeforeUtc = record.NotBeforeUtc; c.NotAfterUtc = record.NotAfterUtc;
        c.IssuedDomainsJson = JsonSerializer.Serialize(record.Domains); c.IssuedRevision = record.Revision;
        c.AriId = record.AriId; c.AriRefreshAtUtc = DateTimeOffset.UtcNow;
        c.RenewAtUtc = RenewalPolicy.Fallback(record.NotBeforeUtc, record.NotAfterUtc);
        c.LatestError = null; c.FailureCount = 0;
    }

    public async Task RefreshAri(CertificateConfig c, CancellationToken token)
    {
        c.AriRefreshAtUtc = DateTimeOffset.UtcNow.AddHours(6);
        try
        {
            var context = new AcmeContext(DirectoryFor(c.Environment));
            var info = await context.GetRenewalInfo(c.AriId!);
            var start = info.SuggestedWindow.Start;
            var end = info.SuggestedWindow.End;
            var proposed = start + (end - start) * Random.Shared.NextDouble();
            var fallback = RenewalPolicy.Fallback(c.NotBeforeUtc!.Value, c.NotAfterUtc!.Value);
            // Do not resample a previously selected time on every poll.
            if (c.RenewAtUtc is null || c.RenewAtUtc > end || c.RenewAtUtc < start)
                c.RenewAtUtc = proposed < fallback ? proposed : fallback;
            var interval = info.RetryAfter ?? TimeSpan.FromHours(6);
            c.AriRefreshAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(interval.TotalSeconds, 60, 43200));
        }
        catch (Exception) when (!token.IsCancellationRequested) { /* fallback retained, retry next interval */ }
        await db.SaveChangesAsync(token);
    }

    private async Task<string> CreateTxt(DnsCredential credential, string name, string content, CancellationToken token)
    {
        using var request = Cloudflare(credential, HttpMethod.Post, "");
        request.Content = JsonContent.Create(new { type = "TXT", name, content, ttl = 60 });
        using var response = await clients.CreateClient("external").SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new PilotException(502, "DNS_API_FAILED", $"Cloudflare DNS API 返回 {(int)response.StatusCode}，请检查 Zone/Token 权限。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new PilotException(502, "DNS_API_FAILED", "Cloudflare 拒绝创建验证记录。");
        return json.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }
    private HttpRequestMessage Cloudflare(DnsCredential c, HttpMethod method, string suffix)
    {
        var request = new HttpRequestMessage(method, $"https://api.cloudflare.com/client/v4/zones/{Uri.EscapeDataString(c.ZoneId)}/dns_records{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", vault.Unprotect(c.EncryptedToken));
        return request;
    }
    private async Task Track(WorkItem job, ChallengeCleanup entry)
    {
        var entries = JsonSerializer.Deserialize<List<ChallengeCleanup>>(job.CleanupJson ?? "[]")!;
        entries.Add(entry); job.CleanupJson = JsonSerializer.Serialize(entries);
        await db.SaveChangesAsync();
    }
    public async Task Cleanup(WorkItem job)
    {
        var entries = JsonSerializer.Deserialize<List<ChallengeCleanup>>(job.CleanupJson ?? "[]")!;
        foreach (var entry in entries.ToArray())
        {
            if (entry.Type == "http")
            {
                var file = ConfigValidation.SafePath(entry.Target, options.WebRoots);
                if (File.Exists(file)) File.Delete(file);
            }
            else
            {
                var c = await db.Credentials.FindAsync(entry.CredentialId)
                    ?? throw new PilotException(409, "CLEANUP_PENDING", "清理 DNS 挑战需要原凭据。");
                using var request = Cloudflare(c, HttpMethod.Delete, "/" + Uri.EscapeDataString(entry.Target));
                using var response = await clients.CreateClient("external").SendAsync(request);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    throw new PilotException(502, "CLEANUP_PENDING", "验证记录清理失败，将保留记录 ID 等待恢复。");
            }
            entries.Remove(entry); job.CleanupJson = JsonSerializer.Serialize(entries); await db.SaveChangesAsync();
        }
    }
}
