using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace CertPilot;

public static partial class ConfigValidation
{
    [GeneratedRegex(@"^(?:\*\.)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
    public static string NormalizeDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw Invalid("域名不能为空。");
        input = input.Trim().TrimEnd('.').ToLowerInvariant();
        var wildcard = input.StartsWith("*.", StringComparison.Ordinal);
        try { input = (wildcard ? "*." : "") + new IdnMapping().GetAscii(wildcard ? input[2..] : input); }
        catch (ArgumentException) { throw Invalid("域名格式无效。"); }
        if (input.Length > 253 || !DomainPattern().IsMatch(input) || IPAddress.TryParse(input, out _))
            throw Invalid("请填写完整域名，不含协议、端口或路径。");
        return input;
    }
    public static CertificateInput Validate(CertificateInput input, PilotOptions options)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80) throw Invalid("名称长度为 1–80 字符。");
        if (string.IsNullOrWhiteSpace(input.Email) || !MailAddress.TryCreate(input.Email, out var email) || email.Address != input.Email) throw Invalid("邮箱格式无效。");
        if (input.Domains is null || input.Domains.Length is < 1 or > 100) throw Invalid("域名数量为 1–100。");
        var domains = input.Domains.Select(NormalizeDomain).Distinct().ToArray();
        if (input.Environment is not ("staging" or "production")) throw Invalid("请选择测试或正式环境。");
        if (input.Validation is not ("http" or "dns")) throw Invalid("未知验证方式。");
        if (input.Format is not ("pem" or "pfx" or "both")) throw Invalid("未知输出格式。");
        if (input.Validation == "http" && domains.Any(d => d.StartsWith("*."))) throw Invalid("通配符必须使用 DNS-01。");
        if (input.Validation == "dns" && string.IsNullOrWhiteSpace(input.CredentialId)) throw Invalid("请选择 DNS 凭据。");
        ValidateFileName(input.FileName);
        var output = SafePath(input.OutputDirectory, [options.EffectiveOutputRoot, .. options.OutputRoots]);
        if (input.Validation == "http")
            SafePath(input.WebRoot ?? "", options.WebRoots);
        return input with { Name = input.Name.Trim(), Domains = domains, OutputDirectory = output };
    }
    public static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name is "." or ".." ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith(' ') || name.EndsWith('.') ||
            Regex.IsMatch(name, @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw Invalid("文件名含非法字符或 Windows 保留名称。");
    }
    public static string SafePath(string candidate, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal))
            throw Invalid("只允许服务器本地绝对路径。");
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!roots.Select(Path.GetFullPath).Any(root => full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison) ||
            full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
            throw Invalid("目录不在管理员允许的根目录内，请在服务器配置 OutputRoots / WebRoots。");
        if (OperatingSystem.IsWindows())
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("不允许符号链接或目录重解析点。");
        return full;
    }
    public static PilotException Invalid(string message) => new(422, "VALIDATION_FAILED", message);
}
