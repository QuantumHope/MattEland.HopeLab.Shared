using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;

namespace MattEland.HopeLab.Tests;

/// <summary>An app's own secret storage (Hymnal's credential store) in place of the token file.</summary>
public sealed class FakeTokenStore : ITokenStore
{
    public string? Token { get; set; }
    public string? Read() => Token;
    public Task<Result<Unit>> SaveAsync(string token) { Token = token; return Task.FromResult(Result<Unit>.Ok(Unit.Value)); }
    public Result<Unit> Clear() { Token = null; return Result<Unit>.Ok(Unit.Value); }
    public string Description => "the test's credential store";
}

[Collection(EnvironmentCollection.Name)]
public sealed class TokenStoreAndAptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hopelab-store-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task An_apps_own_token_store_is_used_instead_of_the_file()
    {
        var before = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            var gh = new FakeGitHub();
            gh.Json("https://api.github.com/repos/o/r/releases?per_page=1", "[]");
            var store = new FakeTokenStore();
            var options = new AppUpdateOptions("Demo", "o/r", Path.Combine(_dir, "home"), "Demo")
            {
                InstallDirectory = _dir, GitHubCliToken = () => null, TokenStore = store,
            };
            var updater = new AppUpdater(options, new FixedClock(Now), new HttpClient(gh));

            Assert.False(updater.HasSavedToken);
            Assert.True((await updater.SaveTokenAsync("github_pat_x")).IsSuccess);
            Assert.Equal("github_pat_x", store.Token);
            Assert.Equal(new GitHubTokenFound("github_pat_x", "saved token"), updater.FindToken());
            Assert.False(File.Exists(options.TokenFile)); // nothing in plain text

            Assert.True(updater.ClearToken().IsSuccess);
            Assert.Null(store.Token);
            Assert.False(updater.HasSavedToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", before.Item1);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", before.Item2);
        }
    }

    [Theory]
    [InlineData(126, "still installed")]      // pkexec: prompt dismissed, apt-get never ran
    [InlineData(127, "still installed")]      // pkexec: authentication failed or missing
    [InlineData(100, "may be incomplete")]    // apt-get ran and failed: dpkg may have replaced files
    public async Task A_failed_package_install_says_whether_anything_may_have_changed(int exit, string expected)
    {
        var install = Path.Combine(_dir, "opt", "demo");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName),
            new ReleaseStamp("Demo", "0.1.1", "linux-x64", "o/r", null, Now, "deb").ToJson());
        var deb = Path.Combine(_dir, "demo_0.1.2_amd64.deb");
        File.WriteAllText(deb, "x");
        var updater = new AppUpdater(new AppUpdateOptions("Demo", "o/r", Path.Combine(_dir, "home"), "Demo")
        {
            InstallDirectory = install, GitHubCliToken = () => null,
        }, new FixedClock(Now), new HttpClient(new FakeGitHub()))
        {
            SudoWithoutPassword = () => false,
            RunElevated = (_, _) => exit,
        };
        var offer = new UpdateOffer(new(0, 1, 2), new(0, 1, 1), "v0.1.2", "", null, Now, "linux-x64.deb",
            new("demo_0.1.2_amd64.deb", "00", 1), new(1, "x", 1, "", ""));

        var r = await updater.InstallAsync(offer, deb);

        Assert.False(r.IsSuccess);
        Assert.Contains(expected, r.Error);
        Assert.Contains("sudo apt install", r.Error);
    }
}
