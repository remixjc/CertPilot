using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CertPilot;

public static class SiteInspection
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xe0) == 0x20 && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal &&
                !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return !(b[0] is 0 or 10 or 127 or >= 224 || b[0] == 169 && b[1] == 254 ||
            b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
            b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19 ||
            b[0] == 192 && b[1] == 0 || b[0] == 198 && b[1] == 51 || b[0] == 203 && b[1] == 0);
    }
    public static async Task<string> Run(CertificateConfig c, string domain, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(domain, token);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new PilotException(422, "TARGET_NOT_PUBLIC", "检测仅允许解析到公网的已托管域名。");
        var results = new List<object>();
        foreach (var address in addresses.Distinct().Take(8))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var tcp = new TcpClient(address.AddressFamily);
                await tcp.ConnectAsync(address, 443, timeout.Token); // pin checked IP; no DNS rebinding
                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = domain, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                using var cert = X509CertificateLoader.LoadCertificate(tls.RemoteCertificate!.GetRawCertData());
                results.Add(new { address = address.ToString(), status = "valid", cert.Thumbprint,
                    notAfterUtc = cert.NotAfter.ToUniversalTime(), matchesManaged = cert.Thumbprint == c.Thumbprint });
            }
            catch (Exception e) when (e is AuthenticationException or SocketException or IOException or OperationCanceledException)
            { results.Add(new { address = address.ToString(), status = "failed", error = "TLS 连接、信任链或域名验证失败。" }); }
        }
        return JsonSerializer.Serialize(new { domain, observedFrom = "server", observedAtUtc = DateTimeOffset.UtcNow,
            nodes = results, truncated = addresses.Length > 8 });
    }
}
