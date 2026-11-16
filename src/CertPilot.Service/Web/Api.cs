using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public static class Api
{
    private static string Subject(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString() is { Length: >= 8 and <= 128 } key
        ? key : throw new PilotException(428, "IDEMPOTENCY_KEY_REQUIRED", "请提供 8–128 字符 Idempotency-Key。");
    private static int Revision(HttpContext c)
    {
        var match = System.Text.RegularExpressions.Regex.Match(c.Request.Headers.IfMatch.ToString(), "^\"revision-(\\d+)\"$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var revision) ? revision
            : throw new PilotException(428, "IF_MATCH_REQUIRED", "请提供 If-Match: \"revision-N\"。");
    }
    private static void ETag(HttpContext c, int revision) => c.Response.Headers.ETag = $"\"revision-{revision}\"";
    private static object CredentialView(DnsCredential c) => new { c.Id, c.Revision, c.Name, c.Provider, c.ZoneId, c.ZoneName };
    private static object JobView(WorkItem j) => new { j.Id, j.CertificateId, j.Kind, j.Status, j.Stage, j.Attempt,
        j.CreatedAtUtc, j.FinishedAtUtc, j.NextAttemptAtUtc, j.ErrorCode, j.Error,
        result = j.Status == "succeeded" && j.ResultJson is not null ? JsonSerializer.Deserialize<JsonElement>(j.ResultJson) : (JsonElement?)null };
    private static object Page<T>(IEnumerable<T> source, int? limit, string? cursor)
    {
        var count = Math.Clamp(limit ?? 20, 1, 100);
        var skip = 0;
        if (cursor is not null && (!int.TryParse(cursor, out skip) || skip < 0)) throw ConfigValidation.Invalid("无效分页游标。");
        var items = source.Skip(skip).Take(count + 1).ToArray();
        return new { items = items.Take(count), nextCursor = items.Length > count ? (skip + count).ToString() : null };
    }

    public static void Map(WebApplication app, PilotOptions options, RuntimeState runtime)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/health/ready", async (PilotDb db) =>
            await db.Database.CanConnectAsync() && (options.DisableWorker || runtime.WorkerHeartbeatUtc > DateTimeOffset.UtcNow.AddMinutes(-2))
                ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503)).RequireAuthorization("system:read");
        var api = app.MapGroup("/api/v1");
        api.MapGet("/auth/csrf", (IAntiforgery antiforgery, HttpContext c) => new { token = antiforgery.GetAndStoreTokens(c).RequestToken });
        api.MapPost("/auth/login", async (LoginInput input, UserManager<PilotUser> users, SignInManager<PilotUser> signIn, HttpContext c) =>
        {
            var user = await users.FindByNameAsync(input.Username ?? "");
            var result = user is null ? SignInResult.Failed : await signIn.CheckPasswordSignInAsync(user, input.Password ?? "", true);
            if (!result.Succeeded) return Results.Problem(statusCode: 401, title: "LOGIN_FAILED", detail: "账号、密码错误或账号已锁定。");
            await c.SignInAsync(IdentityConstants.ApplicationScheme, await signIn.CreateUserPrincipalAsync(user!));
            return Results.Ok(new { mustChangePassword = user!.MustChangePassword });
        }).RequireRateLimiting("login");
        api.MapGet("/auth/me", async (UserManager<PilotUser> users, HttpContext c) =>
        {
            var user = (await users.GetUserAsync(c.User))!;
            return new { user.UserName, user.MustChangePassword, roles = await users.GetRolesAsync(user) };
        }).RequireAuthorization("person");
        api.MapPost("/auth/logout", async (HttpContext c) => { await c.SignOutAsync(IdentityConstants.ApplicationScheme); return Results.NoContent(); }).RequireAuthorization("person");
        api.MapPost("/auth/password", async (PasswordInput input, UserManager<PilotUser> users, SignInManager<PilotUser> signIn, HttpContext c) =>
        {
            var user = (await users.GetUserAsync(c.User))!;
            var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
            if (!result.Succeeded) throw ConfigValidation.Invalid(string.Join(" ", result.Errors.Select(e => e.Description)));
            user.MustChangePassword = false; await users.UpdateAsync(user);
            await c.SignInAsync(IdentityConstants.ApplicationScheme, await signIn.CreateUserPrincipalAsync(user));
            var file = Path.Combine(options.DataDirectory, "bootstrap-admin.txt");
            if (user.UserName == "admin" && File.Exists(file)) File.Delete(file);
            return Results.NoContent();
        }).RequireAuthorization("person");
        api.MapGet("/system/status", async (PilotDb db) => new { service = runtime.Snapshot(),
            queued = await db.Jobs.CountAsync(j => j.Status == "queued"), running = await db.Jobs.CountAsync(j => j.Status == "running"),
            outputRoots = new[] { options.EffectiveOutputRoot }.Concat(options.OutputRoots), options.WebRoots,
            listen = new { options.ListenAddress, options.Port, https = !options.DevelopmentHttp } }).RequireAuthorization("system:read");
        api.MapGet("/certificates", async (PilotDb db, int? limit, string? cursor, string? search) =>
            Page((await db.Certificates.AsNoTracking().ToListAsync()).Where(c => search is null || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name).ThenBy(c => c.Id).Select(CertificateStore.View), limit, cursor)).RequireAuthorization("certificates:read");
        api.MapGet("/certificates/{id}", async (string id, PilotDb db, HttpContext c) =>
        {
            var item = await db.Certificates.FindAsync(id) ?? throw new PilotException(404, "NOT_FOUND", "证书不存在。");
            ETag(c, item.Revision); return CertificateStore.View(item);
        }).RequireAuthorization("certificates:read");
        api.MapPost("/certificates", async (CertificateInput input, CertificateStore store, PilotDb db, HttpContext c) =>
        {
            var key = Key(c); var subject = Subject(c); var hash = SecretVault.Hash(JsonSerializer.Serialize(input));
            var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.Subject == subject && r.Key == key);
            if (receipt is not null)
            {
                if (receipt.Hash != hash) throw new PilotException(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他内容。");
                var existing = await db.Certificates.FindAsync(receipt.EntityId) ?? throw new PilotException(409, "RESOURCE_REMOVED", "原配置已移除。");
                ETag(c, existing.Revision); return Results.Created($"/api/v1/certificates/{existing.Id}", CertificateStore.View(existing));
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            var saved = await store.Save(null, input, null, subject);
            db.Receipts.Add(new() { Subject = subject, Key = key, Hash = hash, EntityId = saved.Id });
            await db.SaveChangesAsync(); await transaction.CommitAsync(); ETag(c, saved.Revision);
            return Results.Created($"/api/v1/certificates/{saved.Id}", CertificateStore.View(saved));
        }).RequireAuthorization("certificates:write");
        api.MapPut("/certificates/{id}", async (string id, CertificateInput input, CertificateStore store, HttpContext c) =>
        { var saved = await store.Save(id, input, Revision(c), Subject(c)); ETag(c, saved.Revision); return CertificateStore.View(saved); }).RequireAuthorization("certificates:write");
        api.MapDelete("/certificates/{id}", async (string id, PilotDb db, HttpContext c) =>
        {
            var revision = Revision(c);
            var item = await db.Certificates.FindAsync(id) ?? throw new PilotException(404, "NOT_FOUND", "证书不存在。");
            if (item.Revision != revision) throw new PilotException(412, "REVISION_CONFLICT", "配置已更改。");
            if (await db.Jobs.AnyAsync(j => j.CertificateId == id && (j.Status == "running" || j.Status == "queued" || j.CleanupJson != null && j.CleanupJson != "[]")))
                throw new PilotException(409, "JOB_ACTIVE", "仍有活动任务或挑战记录等待清理。");
            db.Certificates.Remove(item); db.Events.Add(new() { Subject = Subject(c), Action = "certificate.removed", EntityId = id });
            await db.SaveChangesAsync(); return Results.NoContent();
        }).RequireAuthorization("certificates:write");
        foreach (var (route, kind) in new[] { ("issuances", "issuance"), ("renewals", "renewal"), ("inspections", "inspection") })
            api.MapPost("/certificates/{id}/" + route, async (string id, JobRequest input, JobQueue queue, HttpContext c) =>
            {
                var job = await queue.Enqueue(id, kind, Subject(c), Key(c), input.Domain);
                c.Response.Headers.RetryAfter = "3";
                return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, job.Status, statusUrl = $"/api/v1/jobs/{job.Id}" });
            }).RequireAuthorization("jobs:execute");
        api.MapGet("/jobs/{id}", async (string id, PilotDb db) => JobView(await db.Jobs.FindAsync(id)
            ?? throw new PilotException(404, "NOT_FOUND", "任务不存在。"))).RequireAuthorization("certificates:read");
        api.MapGet("/jobs", async (PilotDb db, int? limit, string? cursor) => Page((await db.Jobs.AsNoTracking().ToListAsync())
            .OrderByDescending(j => j.CreatedAtUtc).Select(JobView), limit, cursor)).RequireAuthorization("certificates:read");
        api.MapGet("/certificates/{id}/events", async (string id, PilotDb db, int? limit, string? cursor) =>
            Page((await db.Events.Where(e => e.EntityId == id).ToListAsync()).OrderByDescending(e => e.Id), limit, cursor)).RequireAuthorization("certificates:read");
        api.MapGet("/credentials", async (PilotDb db) => new { items = (await db.Credentials.ToListAsync()).Select(CredentialView) }).RequireAuthorization("credentials:write");
        api.MapPost("/credentials", async (CredentialInput input, PilotDb db, SecretVault vault, HttpContext c) =>
        {
            ValidateCredential(input);
            var saved = new DnsCredential { Name = input.Name.Trim(), ZoneId = input.ZoneId, ZoneName = ConfigValidation.NormalizeDomain(input.ZoneName), EncryptedToken = vault.Protect(input.Token) };
            db.Credentials.Add(saved); db.Events.Add(new() { Subject = Subject(c), Action = "credential.created", EntityId = saved.Id });
            await db.SaveChangesAsync(); ETag(c, saved.Revision); return Results.Created($"/api/v1/credentials/{saved.Id}", CredentialView(saved));
        }).RequireAuthorization("credentials:write");
        api.MapPut("/credentials/{id}", async (string id, CredentialInput input, PilotDb db, SecretVault vault, HttpContext c) =>
        {
            ValidateCredential(input); var revision = Revision(c);
            var saved = await db.Credentials.FindAsync(id) ?? throw new PilotException(404, "NOT_FOUND", "凭据不存在。");
            if (saved.Revision != revision) throw new PilotException(412, "REVISION_CONFLICT", "凭据已更改。");
            if (await db.Jobs.AnyAsync(j => j.Status == "running")) throw new PilotException(409, "JOB_ACTIVE", "请等待任务完成后替换凭据。");
            if (saved.ZoneId != input.ZoneId || saved.ZoneName != ConfigValidation.NormalizeDomain(input.ZoneName))
                throw ConfigValidation.Invalid("替换仅支持同一 Zone；更换 Zone 请新建凭据。");
            saved.Name = input.Name.Trim(); saved.EncryptedToken = vault.Protect(input.Token); saved.Revision++;
            db.Events.Add(new() { Subject = Subject(c), Action = "credential.updated", EntityId = id });
            await db.SaveChangesAsync(); ETag(c, saved.Revision); return CredentialView(saved);
        }).RequireAuthorization("credentials:write");
        api.MapGet("/api-tokens", async (PilotDb db) => new { items = await db.Tokens.Select(t => new { t.Id, t.Name, t.Scopes, t.ExpiresAtUtc, t.Revoked }).ToListAsync() }).RequireAuthorization("admin");
        api.MapPost("/api-tokens", async (TokenInput input, PilotDb db, HttpContext c) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.Days is < 1 or > 365 || input.Scopes is null || input.Scopes.Length == 0 || input.Scopes.Any(s => !PilotSecurity.Scopes.Contains(s)))
                throw ConfigValidation.Invalid("Token 名称、有效期或范围无效。");
            var value = SecretVault.NewToken();
            var saved = new ServiceToken { Name = input.Name.Trim(), Hash = SecretVault.Hash(value), Scopes = string.Join(' ', input.Scopes.Distinct()), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(input.Days) };
            db.Tokens.Add(saved); db.Events.Add(new() { Subject = Subject(c), Action = "token.created", EntityId = saved.Id });
            await db.SaveChangesAsync(); return Results.Created($"/api/v1/api-tokens/{saved.Id}", new { saved.Id, token = value, saved.ExpiresAtUtc });
        }).RequireAuthorization("admin");
        api.MapDelete("/api-tokens/{id}", async (string id, PilotDb db, HttpContext c) =>
        {
            var saved = await db.Tokens.FindAsync(id) ?? throw new PilotException(404, "NOT_FOUND", "Token 不存在。");
            saved.Revoked = true; db.Events.Add(new() { Subject = Subject(c), Action = "token.revoked", EntityId = id }); await db.SaveChangesAsync(); return Results.NoContent();
        }).RequireAuthorization("admin");
        api.MapGet("/users", async (UserManager<PilotUser> users) => new { items = await users.Users.Select(u => new { u.Id, u.UserName, u.MustChangePassword }).ToListAsync() }).RequireAuthorization("admin");
        api.MapPost("/users", async (UserInput input, UserManager<PilotUser> users, PilotDb db, HttpContext c) =>
        {
            if (input.Role is not ("Administrator" or "Operator" or "Viewer") || string.IsNullOrWhiteSpace(input.Username)) throw ConfigValidation.Invalid("账号或角色无效。");
            var user = new PilotUser { UserName = input.Username.Trim(), LockoutEnabled = true };
            var result = await users.CreateAsync(user, input.Password);
            if (!result.Succeeded) throw ConfigValidation.Invalid(string.Join(" ", result.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, input.Role); db.Events.Add(new() { Subject = Subject(c), Action = "user.created", EntityId = user.Id }); await db.SaveChangesAsync();
            return Results.Created("/api/v1/users/" + user.Id, new { user.Id, user.UserName, input.Role });
        }).RequireAuthorization("admin");
    }
    private static void ValidateCredential(CredentialInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || string.IsNullOrWhiteSpace(input.Token) || input.Token.Length > 512 ||
            !System.Text.RegularExpressions.Regex.IsMatch(input.ZoneId ?? "", "^[a-fA-F0-9]{32}$")) throw ConfigValidation.Invalid("凭据名称、Token 或 Zone ID 无效。");
        if (ConfigValidation.NormalizeDomain(input.ZoneName).StartsWith("*.")) throw ConfigValidation.Invalid("Zone 名不能使用通配符。");
    }
}
