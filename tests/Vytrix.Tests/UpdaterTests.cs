using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>Security and correctness of the auto-update decision logic (no network).</summary>
public class UpdaterTests
{
    private static readonly string Sha = new('A', 64);
    private static string Manifest(string version, string url, string sha = "") =>
        $$"""{ "version": "{{version}}", "url": "{{url}}", "sha256": "{{(sha == "" ? Sha : sha)}}", "notes": "n" }""";
    private const string GoodUrl = "https://github.com/owner/VYTRIX/releases/download/v1.2.0/VYTRIX-Setup-1.2.0.exe";

    [Fact]
    public void Newer_version_from_github_is_offered()
    {
        var (m, msg) = Updater.Evaluate(Manifest("1.2.0", GoodUrl), new Version(1, 0, 0));
        Assert.NotNull(m);
        Assert.Contains("1.2.0", msg);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.9")]
    public void Same_or_older_version_is_not_offered(string v) =>
        Assert.Null(Updater.Evaluate(Manifest(v, GoodUrl), new Version(1, 0, 0)).Manifest);

    [Theory]
    [InlineData("https://evil.example.com/VYTRIX-Setup.exe")]
    [InlineData("http://github.com/owner/VYTRIX/releases/download/v1.2.0/x.exe")]   // not HTTPS
    [InlineData("https://github.com.evil.example/x.exe")]                           // look-alike host
    public void Installers_outside_github_https_are_rejected(string url) =>
        Assert.Null(Updater.Evaluate(Manifest("9.9.9", url), new Version(1, 0, 0)).Manifest);

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    public void Manifest_without_a_full_sha256_is_rejected(string sha) =>
        Assert.Null(Updater.Evaluate(Manifest("9.9.9", GoodUrl, sha == "" ? " " : sha), new Version(1, 0, 0)).Manifest);

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"version\": \"banana\", \"url\": \"https://github.com/x\", \"sha256\": \"x\" }")]
    public void Garbage_manifest_is_rejected_without_throwing(string json) =>
        Assert.Null(Updater.Evaluate(json, new Version(1, 0, 0)).Manifest);

    [Fact]
    public void Default_feed_points_at_latest_github_release()
    {
        Assert.StartsWith("https://github.com/", Updater.DefaultFeed);
        Assert.EndsWith("/releases/latest/download/latest.json", Updater.DefaultFeed);
        Assert.DoesNotContain("OWNER", Updater.Repository);   // must be the real repository before release
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("https://raw.githubusercontent.com/x/y/main/evil.exe", false)]   // repo file, not a release asset
    public void Only_github_release_hosts_are_trusted(string url, bool trusted) =>
        Assert.Equal(trusted, Updater.IsTrustedDownload(new Uri(url)));
}
