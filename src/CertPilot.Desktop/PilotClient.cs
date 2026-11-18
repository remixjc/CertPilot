using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CertPilot.Desktop;

public sealed class PilotClient : IDisposable
{
    private readonly HttpClient http;
    private string? csrf;
    public Uri Endpoint { get; }
    public PilotClient(string address, string dataDirectory)
    {
        Endpoint = new Uri(address.Trim().TrimEnd('/') + "/");
        if (Endpoint.Scheme != "https" && !(Endpoint.Scheme == "http" && Endpoint.IsLoopback))
            throw new InvalidOperationException("远程连接必须使用 HTTPS；HTTP 只允许本机开发。");
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var publicCertificate = Path.Combine(dataDirectory, "management.cer");
        // Only pin the exact local instance certificate, never bypass TLS validation for remote hosts.
        if (Endpoint.IsLoopback && File.Exists(publicCertificate))
        {
            var expected = File.ReadAllBytes(publicCertificate);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == SslPolicyErrors.None || cert is not null &&
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                cert.NotAfter.ToUniversalTime() > DateTime.UtcNow && cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                cert.RawData.AsSpan().SequenceEqual(expected);
        }
        http = new HttpClient(handler) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(30) };
    }
    public async Task RefreshCsrf()
    {
        var response = await Get<JsonElement>("auth/csrf");
        csrf = response.GetProperty("token").GetString();
    }
    public Task<T> Get<T>(string path) => Send<T>(HttpMethod.Get, path);
    public async Task<T> Write<T>(string path, HttpMethod method, object? body = null, int? revision = null)
    {
        if (csrf is null) await RefreshCsrf();
        return await Send<T>(method, path, body, revision);
    }
    private async Task<T> Send<T>(HttpMethod method, string path, object? body = null, int? revision = null)
    {
        using var request = new HttpRequestMessage(method, "api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"revision-{revision}\"");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            var message = response.StatusCode == HttpStatusCode.Unauthorized ? "尚未登录或会话已过期，请重新登录。" : $"请求失败：HTTP {(int)response.StatusCode}";
            string? code = null;
            try
            {
                using var problem = JsonDocument.Parse(text);
                if (problem.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String) message = detail.GetString()!;
                    if (problem.RootElement.TryGetProperty("code", out var errorCode) && errorCode.ValueKind == JsonValueKind.String) code = errorCode.GetString();
                }
            }
            catch (JsonException) { }
            throw new PilotClientException(response.StatusCode, message, code);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    public async Task<List<T>> All<T>(string path)
    {
        var items = new List<T>(); string? cursor = null;
        do
        {
            var page = await Get<Page<T>>($"{path}?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            items.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        return items;
    }
    public void Dispose() => http.Dispose();
}
public sealed class PilotClientException(HttpStatusCode status, string message, string? code = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;
}
public sealed record Page<T>(List<T> Items, string? NextCursor);
public sealed record UserInfo(string UserName, bool MustChangePassword, string[] Roles)
{
    public bool CanWrite => Roles.Any(r => r is "Operator" or "Administrator");
}
public sealed record CertificateInput(string Name, string[] Domains, string Email, string Environment,
    string Validation, string? CredentialId, string? WebRoot, string OutputDirectory, string FileName,
    string Format, bool AutoRenew, bool TermsAccepted);
public sealed class CertificateItem
{
    public string Id { get; set; } = "";
    public int Revision { get; set; }
    public string Name { get; set; } = "";
    public string[] Domains { get; set; } = [];
    public string Email { get; set; } = "";
    public string Environment { get; set; } = "staging";
    public string Validation { get; set; } = "dns";
    public string? CredentialId { get; set; }
    public string? WebRoot { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "both";
    public bool AutoRenew { get; set; }
    public bool TermsAccepted { get; set; }
    public CertificateState Certificate { get; set; } = new();
    public RenewalState Renewal { get; set; } = new();
    public string DomainText => string.Join(" · ", Domains);
    public string StateText => Certificate.Status switch { "valid" => "有效", "expired" => "已过期", _ => "待签发" };
    public string ExpiryText => Certificate.NotAfterUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未签发";
    public string RenewalText => !AutoRenew ? "已暂停" : Renewal.Error is not null ? "需关注" : "已开启";
    public string EnvironmentText => Environment == "production" ? "正式" : "测试";
    public CertificateInput Input(bool? renew = null) => new(Name, Domains, Email, Environment, Validation,
        CredentialId, WebRoot, OutputDirectory, FileName, Format, renew ?? AutoRenew, TermsAccepted);
}
public sealed class CertificateState
{
    public string Status { get; set; } = "notIssued";
    public string[] Domains { get; set; } = [];
    public DateTimeOffset? NotAfterUtc { get; set; }
    public string? Thumbprint { get; set; }
    public string? CurrentGeneration { get; set; }
    public int? IssuedRevision { get; set; }
}
public sealed class RenewalState
{
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? Error { get; set; }
}
public sealed record CredentialItem(string Id, int Revision, string Name, string ZoneName, string ZoneId);
public sealed class JobItem
{
    public string Id { get; set; } = "";
    public string CertificateId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? Error { get; set; }
    public JsonElement? Result { get; set; }
    public string KindText => Kind switch { "issuance" => "证书申请", "renewal" => "自动续期", _ => "网站检测" };
    public string StatusText => Status switch { "queued" => "排队中", "running" => "运行中", "succeeded" => "已完成", _ => "失败" };
    public string CreatedText => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
public sealed class SystemInfo
{
    public string[] OutputRoots { get; set; } = [];
    public string[] WebRoots { get; set; } = [];
    public int Queued { get; set; }
    public int Running { get; set; }
    public JsonElement Service { get; set; }
}
public sealed class DesktopSettings
{
    public string Endpoint { get; set; } = "https://localhost:8443";
    public string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CertPilot");
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertPilot.Desktop", "settings.json");
    public static DesktopSettings Load()
    {
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
