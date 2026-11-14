using MattEland.HopeLab.Git;
using MattEland.HopeLab.Updates;

namespace MattEland.HopeLab.Tests;

/// <summary>Cases from the review of the first version of this repository.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hopelab-review-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(@"""/home/me/caf\303\251 repo""", "/home/me/café repo")]
    [InlineData(@"""C:/src/a\""quote\\back\tslash""", "C:/src/a\"quote\\back\tslash")]
    [InlineData("/plain/path with spaces", "/plain/path with spaces")]
    public void Worktree_paths_git_quoted_are_decoded(string listed, string expected)
    {
        var entries = WorktreeListParser.Parse($"worktree {listed}\nHEAD abc\nbranch refs/heads/main\n\n");
        Assert.Equal(expected, Assert.Single(entries).Path);
    }

    [Theory]
    [InlineData("""{ "app": "A", "version": "0.1.2", "tag": "v0.1.2" }""")]                                              // no assets
    [InlineData("""{ "app": "A", "version": "0.1.2", "tag": "v0.1.2", "assets": null }""")]
    [InlineData("""{ "app": "A", "version": "0.1.2", "tag": "v0.1.2", "assets": { "win-x64": null } }""")]
    [InlineData("""{ "app": "A", "version": "0.1.2", "tag": "v0.1.2", "assets": { "win-x64": { "name": "a.zip", "sha256": null, "size": 1 } } }""")]
    [InlineData("""{ "app": "A", "version": null, "tag": "v0.1.2", "assets": {} }""")]
    public void A_manifest_missing_its_essentials_is_unreadable_not_a_crash_later(string json) =>
        Assert.Null(ReleaseManifest.Parse(json));

    [Fact]
    public void Access_problems_are_told_from_network_ones()
    {
        var gh = new GitHubReleases(new HttpClient(new FakeGitHub()), "o/r", () => null);
        Assert.True(GitHubReleases.IsAccessProblem(gh.Explain(System.Net.HttpStatusCode.NotFound, hadToken: false)));
        Assert.True(GitHubReleases.IsAccessProblem(gh.Explain(System.Net.HttpStatusCode.Unauthorized, hadToken: true)));
        Assert.False(GitHubReleases.IsAccessProblem("Couldn't reach GitHub: No such host is known."));
        Assert.False(GitHubReleases.IsAccessProblem(null));
    }

    [Fact]
    public async Task A_staged_build_of_another_version_is_refused_before_anything_stops()
    {
        var install = Path.Combine(_dir, "install");
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), new ReleaseStamp("Demo", "0.1.1", "win-x64", "o/r", null, Now).ToJson());
        File.WriteAllText(Path.Combine(staging, ReleaseStamp.FileName), new ReleaseStamp("Demo", "0.1.3", "win-x64", "o/r", null, Now).ToJson());
        var stopped = false;
        var updater = Updater(install, () => stopped = true);
        var offer = Offer("0.1.2", "win-x64");

        var r = await updater.InstallAsync(offer, staging);

        Assert.False(r.IsSuccess);
        Assert.Contains("0.1.3, not 0.1.2", r.Error);
        Assert.False(stopped);
    }

    [Fact]
    public async Task Installing_from_a_folder_the_updater_does_not_own_leaves_that_folder_and_its_neighbours_alone()
    {
        var work = Path.Combine(_dir, "work");
        var install = Path.Combine(work, "install");
        var staging = Path.Combine(work, "staging");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), new ReleaseStamp("Demo", "0.1.1", "win-x64", "o/r", null, Now).ToJson());
        File.WriteAllText(Path.Combine(staging, ReleaseStamp.FileName), new ReleaseStamp("Demo", "0.1.2", "win-x64", "o/r", null, Now).ToJson());
        File.WriteAllText(Path.Combine(work, "unrelated.txt"), "keep me");

        var r = await Updater(install, () => { }).InstallAsync(Offer("0.1.2", "win-x64"), staging);

        Assert.True(r.IsSuccess, r.Error);
        Assert.True(File.Exists(Path.Combine(work, "unrelated.txt")));       // the parent folder was not deleted
        Assert.True(File.Exists(Path.Combine(install, ReleaseStamp.FileName))); // nor the freshly updated install
    }

    [Fact]
    public async Task Services_come_back_even_if_the_package_installer_throws()
    {
        var install = Path.Combine(_dir, "opt");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), new ReleaseStamp("Demo", "0.1.1", "linux-x64", "o/r", null, Now, "deb").ToJson());
        var deb = Path.Combine(_dir, "demo_0.1.2_amd64.deb");
        File.WriteAllText(deb, "x");
        var restarted = false;
        var updater = new AppUpdater(new AppUpdateOptions("Demo", "o/r", Path.Combine(_dir, "home"), "Demo")
        {
            InstallDirectory = install,
            GitHubCliToken = () => null,
            StopServices = (_, _) => Task.FromResult<RestartServicesAsync?>(_ => { restarted = true; return Task.FromResult(MattEland.HopeLab.Common.Result.Ok()); }),
        }, new FixedClock(Now), new HttpClient(new FakeGitHub()))
        {
            SudoWithoutPassword = () => false,
            RunElevated = (_, _) => throw new InvalidOperationException("boom"),
        };

        var r = await updater.InstallAsync(Offer("0.1.2", "linux-x64.deb"), deb);

        Assert.False(r.IsSuccess);
        Assert.Contains("boom", r.Error);
        Assert.True(restarted);
        Assert.True(File.Exists(deb)); // not ours to delete: it isn't in the updater's download folder
    }

    private AppUpdater Updater(string install, Action onStop) =>
        new(new AppUpdateOptions("Demo", "o/r", Path.Combine(_dir, "home"), "Demo")
        {
            InstallDirectory = install,
            GitHubCliToken = () => null,
            StopServices = (_, _) => { onStop(); return Task.FromResult<RestartServicesAsync?>(null); },
        }, new FixedClock(Now), new HttpClient(new FakeGitHub()));

    private static UpdateOffer Offer(string version, string key) => new(ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.1"),
        $"v{version}", "", null, Now, key, new($"demo-{version}", "00", 1), new(1, "x", 1, "", ""));
}
