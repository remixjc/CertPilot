using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CertPilot;

public sealed class WebListener(PilotOptions options, RuntimeState runtime, MutationLock mutations,
    IHostEnvironment environment, ILogger<WebListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            WebApplication? web = null;
            try
            {
                web = Build(options, runtime, mutations, environment.ContentRootPath);
                await web.StartAsync(stoppingToken);
                runtime.Web = "running"; runtime.WebError = null;
                await web.WaitForShutdownAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                runtime.Web = "degraded"; runtime.WebError = "Web listener failed; check port, TLS and local configuration.";
                logger.LogError("Web listener failed ({Type}); worker continues. Retrying in 30 seconds.", e.GetType().Name);
            }
            finally { if (web is not null) await web.DisposeAsync(); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        runtime.Web = "stopped";
    }

    public static WebApplication Build(PilotOptions options, RuntimeState runtime, MutationLock mutations, string contentRoot, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot") });
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 64 * 1024;
            var address = IPAddress.Parse(options.ListenAddress);
            if (options.DevelopmentHttp && !IPAddress.IsLoopback(address)) throw new InvalidOperationException("Development HTTP is loopback only.");
            k.Listen(address, options.Port, listen => { if (!options.DevelopmentHttp) listen.UseHttps(Bootstrap.ManagementCertificate(options)); });
        });
        builder.Services.AddPilotCore(options, runtime, mutations);
        PilotSecurity.Add(builder.Services, options);
        builder.Services.AddOpenApi();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 180, Window = TimeSpan.FromMinutes(1) }));
            o.AddFixedWindowLimiter("login", o => { o.PermitLimit = 10; o.Window = TimeSpan.FromMinutes(1); });
        });
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (options.CorsOrigins.Length > 0) p.WithOrigins(options.CorsOrigins).WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "If-Match").WithExposedHeaders("ETag", "Location", "Retry-After");
        }));
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!options.AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; return; }
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/openapi"))
                context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (Exception e) when (!context.Response.HasStarted)
            {
                var status = e is PilotException pe ? pe.Status : e is AntiforgeryValidationException ? 400 : e is BadHttpRequestException ? 400 : 500;
                var code = e is PilotException p ? p.Code : status == 400 ? "INVALID_REQUEST" : "INTERNAL_ERROR";
                var detail = e is PilotException safe ? safe.Message : status == 400 ? "请求或 CSRF token 无效。" : "操作失败，请检查服务日志。";
                if (status == 500) app.Logger.LogError("API request failed ({Type}) trace {Trace}", e.GetType().Name, context.TraceIdentifier);
                await Results.Problem(statusCode: status, title: code, detail: detail,
                    extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
            }
        });
        app.UseDefaultFiles(); app.UseStaticFiles(); app.UseRouting(); app.UseCors(); app.UseRateLimiter();
        app.UseAuthentication(); app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true && !context.User.HasClaim(c => c.Type == "scope") &&
                !context.Request.Path.StartsWithSegments("/api/v1/auth"))
            {
                var user = await context.RequestServices.GetRequiredService<UserManager<PilotUser>>().GetUserAsync(context.User);
                if (user?.MustChangePassword == true) throw new PilotException(403, "PASSWORD_CHANGE_REQUIRED", "首次登录请先修改密码。");
            }
            if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH")
            {
                // Bearer credentials must actually authenticate; an arbitrary header never bypasses cookie CSRF.
                if (context.User.Identity?.AuthenticationType != "service-token")
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                await mutations.Gate.WaitAsync(context.RequestAborted);
                try { await next(); } finally { mutations.Gate.Release(); }
            }
            else await next();
        });
        Api.Map(app, options, runtime);
        app.MapOpenApi().RequireAuthorization("person");
        return app;
    }
}
