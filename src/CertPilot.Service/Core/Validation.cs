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
        var output = ValidateDirectory(input.OutputDirectory, [options.EffectiveOutputRoot, .. options.OutputRoots],
            "OUTPUT_DIRECTORY_INVALID", "证书保存目录", "请使用服务默认保存目录，或请管理员在 OutputRoots 中登记所需目录。");
        var webRoot = input.WebRoot;
        if (input.Validation == "http")
            webRoot = ValidateDirectory(input.WebRoot ?? "", options.WebRoots, "WEB_ROOT_INVALID", "HTTP-01 网站根目录",
                options.WebRoots.Length == 0 ? "服务尚未配置 WebRoots，请管理员登记实际网站根目录。" : "请选择 WebRoots 允许的网站目录。");
        return input with { Name = input.Name.Trim(), Domains = domains, OutputDirectory = output, WebRoot = webRoot };
    }
    private static string ValidateDirectory(string? path, string[] roots, string code, string label, string guidance)
    {
        try { return SafePath(path?.Trim() ?? "", roots); }
        catch (PilotException exception) { throw new PilotException(422, code, $"{label}：{exception.Message}{guidance}"); }
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
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) ||
            (OperatingSystem.IsWindows() && (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))))
            throw Invalid("只允许服务器本地绝对路径。");
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Invalid("路径格式无效。"); }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!roots.Select(Path.GetFullPath).Any(root => full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison) ||
            full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
            throw Invalid("不在服务允许的目录范围内。");
        if (OperatingSystem.IsWindows())
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("不允许符号链接或目录重解析点。");
        return full;
    }
    public static PilotException Invalid(string message) => new(422, "VALIDATION_FAILED", message);
}
