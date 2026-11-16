using CertPilot;

namespace CertPilot.Tests;

public class UnitTest1
{
    [Fact]
    public void NormalizesIdnAndRejectsUnsafeDomains()
    {
        Assert.Equal("xn--bcher-kva.example", ConfigValidation.NormalizeDomain("BÜCHER.example."));
        Assert.Equal("*.example.com", ConfigValidation.NormalizeDomain("*.Example.COM"));
        Assert.Throws<PilotException>(() => ConfigValidation.NormalizeDomain("https://example.com"));
        Assert.Throws<PilotException>(() => ConfigValidation.NormalizeDomain("127.0.0.1"));
    }

    [Fact]
    public void RequiresDnsForWildcardsAndKeepsRenewalFallbackWithinLifetime()
    {
        var options = new PilotOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "certpilot-test-" + Guid.NewGuid()), OutputRoots = [Path.GetTempPath()], WebRoots = [Path.GetTempPath()] };
        var input = new CertificateInput("demo", ["*.example.com"], "ops@example.com", "staging", "http", null, null, Path.GetTempPath(), "demo", "both", true, true);
        Assert.Throws<PilotException>(() => ConfigValidation.Validate(input, options));
        var start = DateTimeOffset.UtcNow; var end = start.AddDays(90); var renew = RenewalPolicy.Fallback(start, end);
        Assert.InRange(renew, start.AddDays(59), start.AddDays(61));
    }

    [Fact]
    public void SafePathRejectsUnapprovedRootsAndReservedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "certpilot-root-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        Assert.Equal(Path.GetFullPath(root), ConfigValidation.SafePath(root, [root]));
        Assert.Throws<PilotException>(() => ConfigValidation.SafePath(Path.GetTempPath(), [Path.Combine(root, "sub")]));
        Assert.Throws<PilotException>(() => ConfigValidation.ValidateFileName("CON"));
    }
}
