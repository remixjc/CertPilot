using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CertPilot;

public sealed class ServiceTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, PilotDb db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var hash = SecretVault.Hash(header[7..]);
        var token = await db.Tokens.SingleOrDefaultAsync(t => t.Hash == hash);
        if (token is null || token.Revoked || token.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return AuthenticateResult.Fail("Invalid token.");
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "token:" + token.Id), new(ClaimTypes.Name, token.Name) };
        claims.AddRange(token.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => new Claim("scope", s)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public static class PilotSecurity
{
    public static readonly string[] Scopes = ["certificates:read", "certificates:write", "jobs:execute", "credentials:write", "system:read"];
    public static void Add(IServiceCollection services, PilotOptions options)
    {
        services.AddAuthentication(o => { o.DefaultScheme = "pilot"; o.DefaultChallengeScheme = "pilot"; })
            .AddPolicyScheme("pilot", "Cookie or service token", o => o.ForwardDefaultSelector = c =>
                c.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "service-token" : IdentityConstants.ApplicationScheme)
            .AddScheme<AuthenticationSchemeOptions, ServiceTokenHandler>("service-token", _ => { })
            .AddCookie(IdentityConstants.ApplicationScheme, o =>
            {
                o.Cookie.Name = "CertPilot.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = options.DevelopmentHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = async c =>
                {
                    var users = c.HttpContext.RequestServices.GetRequiredService<UserManager<PilotUser>>();
                    var user = await users.GetUserAsync(c.Principal!);
                    if (user is null || user.SecurityStamp != c.Principal!.FindFirstValue("AspNet.Identity.SecurityStamp")) c.RejectPrincipal();
                };
            });
        services.AddAuthorization(o =>
        {
            foreach (var scope in Scopes)
            {
                var captured = scope;
                o.AddPolicy(scope, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                    c.User.HasClaim("scope", captured) || c.User.IsInRole("Administrator") ||
                    c.User.IsInRole("Operator") || (captured.EndsWith(":read") && c.User.IsInRole("Viewer"))));
            }
            o.AddPolicy("person", p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
            o.AddPolicy("admin", p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireRole("Administrator"));
        });
        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "CertPilot.Csrf";
            o.Cookie.SecurePolicy = options.DevelopmentHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
    }
}
