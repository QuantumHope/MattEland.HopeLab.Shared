using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;
using Xunit;

namespace MattEland.HopeLab.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.412", 0, 1, 412)]
    [InlineData("0.1.412", 0, 1, 412)]
    [InlineData("0.1.412+abc123", 0, 1, 412)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void Parses_tags_and_assembly_versions(string text, int major, int minor, int patch) =>
        Assert.Equal(new ReleaseVersion(major, minor, patch), ReleaseVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("1.x.3")]
    public void Rejects_what_is_not_a_version(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Compares_numerically_not_as_text()
    {
        Assert.True(ReleaseVersion.Parse("0.1.10") > ReleaseVersion.Parse("0.1.9"));
        Assert.True(ReleaseVersion.Parse("0.2.0") > ReleaseVersion.Parse("0.1.999"));
        Assert.True(ReleaseVersion.Parse("1.0.0") > ReleaseVersion.Parse("0.9.9"));
        Assert.Equal(0, ReleaseVersion.Parse("v0.1.5").CompareTo(ReleaseVersion.Parse("0.1.5")));
    }
}

public class UpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    internal static ReleaseManifest Manifest(string version = "0.1.50", string sha = "00") => new("OneRing", version, $"v{version}", "abc1234", Now, "notes",
        new Dictionary<string, ReleaseAsset>
        {
            ["win-x64"] = new($"OneRing-{version}-win-x64.zip", sha, 100),
            ["linux-x64"] = new($"OneRing-{version}-linux-x64.tar.gz", sha, 100),
        });

    private static GitHubRelease Release(string version = "0.1.50") => new($"v{version}", $"https://github.com/o/r/releases/tag/v{version}", Now, "body",
    [
        new(1, ReleaseManifest.FileName, 10, "https://api.github.com/repos/o/r/releases/assets/1", ""),
        new(2, $"OneRing-{version}-win-x64.zip", 100, "https://api.github.com/repos/o/r/releases/assets/2", ""),
        new(3, $"OneRing-{version}-linux-x64.tar.gz", 100, "https://api.github.com/repos/o/r/releases/assets/3", ""),
    ]);

    [Fact]
    public void A_newer_release_with_a_build_for_this_machine_is_offered()
    {
        var r = UpdateChecker.Evaluate(Manifest(), Release(), ReleaseVersion.Parse("0.1.49"), "linux-x64", Now);
        Assert.True(r.IsSuccess, r.Error);
        var offer = Assert.IsType<UpdateOffer>(r.Value!.Offer);
        Assert.Equal("0.1.50", offer.Version.ToString());
        Assert.Equal("OneRing-0.1.50-linux-x64.tar.gz", offer.Archive.Name);
        Assert.Equal(3, offer.Download.Id);
        Assert.Equal("notes", offer.Notes);
    }

    [Theory]
    [InlineData("0.1.50")]
    [InlineData("0.1.51")] // running a build newer than the latest release (say, from a branch) isn't a downgrade offer
    public void The_same_or_an_older_release_is_not_offered(string current)
    {
        var r = UpdateChecker.Evaluate(Manifest(), Release(), ReleaseVersion.Parse(current), "win-x64", Now);
        Assert.True(r.IsSuccess);
        Assert.False(r.Value!.UpdateAvailable);
        Assert.Equal("0.1.50", r.Value.Latest.ToString());
    }

    [Fact]
    public void A_release_without_a_build_for_this_machine_is_an_error_not_an_offer()
    {
        var r = UpdateChecker.Evaluate(Manifest(), Release(), ReleaseVersion.Parse("0.1.1"), "osx-arm64", Now);
        Assert.False(r.IsSuccess);
        Assert.Contains("osx-arm64", r.Error);
    }

    [Fact]
    public void A_manifest_naming_a_missing_asset_is_an_error()
    {
        var release = Release() with { Assets = [.. Release().Assets.Where(a => !a.Name.EndsWith(".zip"))] };
        var r = UpdateChecker.Evaluate(Manifest(), release, ReleaseVersion.Parse("0.1.1"), "win-x64", Now);
        Assert.False(r.IsSuccess);
        Assert.Contains("doesn't have it", r.Error);
    }

    [Fact]
    public void The_manifest_round_trips_as_camel_case_json()
    {
        var json = Manifest().ToJson();
        Assert.Contains("\"pubDate\"", json);
        Assert.Contains("\"linux-x64\"", json);
        var back = ReleaseManifest.Parse(json)!;
        Assert.Equal("0.1.50", back.Version);
        Assert.Equal("OneRing-0.1.50-win-x64.zip", back.Assets["win-x64"].Name);
        Assert.Null(ReleaseManifest.Parse("not json"));
    }
}

/// <summary>Serves canned GitHub answers by URL and records what was asked.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
    public List<HttpRequestMessage> Requests { get; } = [];

    public void Json(string url, string body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public void Bytes(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Routes.TryGetValue(request.RequestUri!.ToString(), out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class GitHubReleasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onering-updates-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeGitHub _gh = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        _gh.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GitHubReleases Client(string? token = null) => new(new HttpClient(_gh), "o/r", () => token);

    /// <summary>A release whose archive holds <paramref name="files"/> plus a release stamp; returns the archive's SHA-256.</summary>
    private string Publish(string version, string rid, Dictionary<string, string> files, string? shaOverride = null)
    {
        var name = $"OneRing-{version}-{rid}.zip";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files.Append(new(ReleaseStamp.FileName,
                         new ReleaseStamp("OneRing", version, rid, "o/r", "abc", Now).ToJson())))
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
        }
        var bytes = ms.ToArray();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var manifest = new ReleaseManifest("OneRing", version, $"v{version}", "abc", Now, null,
            new Dictionary<string, ReleaseAsset> { [rid] = new(name, shaOverride ?? sha, bytes.Length) });
        _gh.Json("https://api.github.com/repos/o/r/releases/latest", $$"""
            { "tag_name": "v{{version}}", "html_url": "https://github.com/o/r/releases/tag/v{{version}}", "published_at": "2026-11-12T09:00:00Z",
              "body": "Changes", "assets": [
                { "id": 11, "name": "latest.json", "size": 1, "url": "https://api.github.com/repos/o/r/releases/assets/11", "browser_download_url": "x" },
                { "id": 12, "name": "{{name}}", "size": {{bytes.Length}}, "url": "https://api.github.com/repos/o/r/releases/assets/12", "browser_download_url": "y" } ] }
            """);
        _gh.Json("https://api.github.com/repos/o/r/releases/assets/11", manifest.ToJson());
        _gh.Bytes("https://api.github.com/repos/o/r/releases/assets/12", bytes);
        return sha;
    }

    [Fact]
    public async Task Checks_downloads_verifies_and_unpacks_a_release()
    {
        Publish("0.1.60", "win-x64", new() { ["OneRing.dll"] = "new app", ["sub/data.txt"] = "data" });
        var gh = Client(token: "t0ken");
        var check = await new UpdateChecker(gh, new FixedClock(Now)).CheckAsync(ReleaseVersion.Parse("0.1.59"), "win-x64");
        Assert.True(check.IsSuccess, check.Error);
        var offer = check.Value!.Offer!;

        var staged = await UpdatePackage.PrepareAsync(gh, offer, _dir);
        Assert.True(staged.IsSuccess, staged.Error);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(staged.Value!, "OneRing.dll")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(staged.Value!, "sub", "data.txt")));

        // The token goes to GitHub; assets are asked for as bytes (the address that works for private repositories).
        Assert.All(_gh.Requests, r => Assert.Equal("t0ken", r.Headers.Authorization?.Parameter));
        Assert.Contains(_gh.Requests, r => r.Headers.Accept.Any(a => a.MediaType == "application/octet-stream"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_refused_and_deleted()
    {
        Publish("0.1.60", "win-x64", new() { ["a.txt"] = "x" }, shaOverride: new string('0', 64));
        var gh = Client();
        var offer = (await new UpdateChecker(gh, new FixedClock(Now)).CheckAsync(ReleaseVersion.Parse("0.1.1"), "win-x64")).Value!.Offer!;

        var staged = await UpdatePackage.PrepareAsync(gh, offer, _dir);
        Assert.False(staged.IsSuccess);
        Assert.Contains("checksum", staged.Error);
        Assert.False(File.Exists(Path.Combine(_dir, "0.1.60", offer.Archive.Name)));
    }

    [Fact]
    public async Task Without_a_token_a_hidden_repository_gets_sign_in_advice()
    {
        var r = await Client(token: null).LatestAsync();
        Assert.False(r.IsSuccess);
        Assert.Contains("save a GitHub token", r.Error);
        Assert.Contains("gh auth login", r.Error);
        Assert.Null(_gh.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public void Tar_gz_archives_keep_the_execute_bit()
    {
        if (OperatingSystem.IsWindows()) return; // no Unix modes to keep
        Directory.CreateDirectory(_dir);
        var archive = Path.Combine(_dir, "x.tar.gz");
        using (var file = File.Create(archive))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "ring")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream("#!/bin/sh"u8.ToArray()),
            };
            tar.WriteEntry(entry);
        }
        var staged = UpdatePackage.Extract(archive, Path.Combine(_dir, "staging"));
        Assert.True(staged.IsSuccess, staged.Error);
        Assert.True(File.GetUnixFileMode(Path.Combine(staged.Value!, "ring")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Saving_a_token_keeps_one_that_can_see_the_repository_and_drops_one_that_cannot()
    {
        var before = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            Environment.GetEnvironmentVariable(TestApp.TokenVariable));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable(TestApp.TokenVariable, null);
            var home = Path.Combine(_dir, "home");
            var tokenFile = Path.Combine(home, "github-token");
            var updater = TestApp.Updater(home, _dir, new GitHubReleases(new HttpClient(_gh), "o/r", () => GitHubToken.ReadSaved(tokenFile)));

            // Can see the repository, which has no release yet (latest → 404): kept.
            _gh.Json("https://api.github.com/repos/o/r", "{}");
            var ok = await updater.SaveTokenAsync("github_pat_good");
            Assert.True(ok.IsSuccess, ok.Error);
            Assert.Contains("no published release yet", ok.Value);
            Assert.Equal("github_pat_good", GitHubToken.ReadSaved(tokenFile));

            // Can't see it: refused, and nothing left on disk.
            _gh.Routes.Remove("https://api.github.com/repos/o/r");
            var bad = await updater.SaveTokenAsync("github_pat_other_account");
            Assert.False(bad.IsSuccess);
            Assert.Contains("can't see", bad.Error);
            Assert.Equal("github_pat_good", GitHubToken.ReadSaved(tokenFile)); // the good one is put back

            GitHubToken.Clear(tokenFile);
            Assert.False((await updater.SaveTokenAsync("github_pat_other_account")).IsSuccess);
            Assert.Null(GitHubToken.ReadSaved(tokenFile)); // and with none before, nothing is left
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", before.Item1);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", before.Item2);
            Environment.SetEnvironmentVariable(TestApp.TokenVariable, before.Item3);
        }
    }

    [Fact]
    public async Task The_updater_refuses_a_download_for_another_platform_and_a_source_build_does_not_update()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        var home = Path.Combine(_dir, "home");

        var fromSource = TestApp.Updater(home, install, Client());
        Assert.False(fromSource.CanUpdate);
        Assert.Contains("built from source", fromSource.NotUpdatableReason);

        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), new ReleaseStamp("OneRing", "0.1.1", "win-x64", "o/r", null, Now).ToJson());
        var released = TestApp.Updater(home, install, Client());
        Assert.True(released.CanUpdate);
        Assert.Equal("0.1.1", released.CurrentVersion.ToString());

        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ReleaseStamp.FileName), new ReleaseStamp("OneRing", "0.1.2", "linux-x64", "o/r", null, Now).ToJson());
        var offer = new UpdateOffer(new(0, 1, 2), new(0, 1, 1), "v0.1.2", "", null, Now, "win-x64", new("x.zip", "00", 1), new(1, "x.zip", 1, "", ""));
        var r = await released.InstallAsync(offer, staging);
        Assert.False(r.IsSuccess);
        Assert.Contains("nothing was changed", r.Error);
    }
}

public sealed class DebUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onering-deb-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Stamp(string version, string? package) =>
        new ReleaseStamp("OneRing", version, "linux-x64", "o/r", null, Now, package).ToJson();

    [Fact]
    public void The_package_asks_through_sudo_when_it_can_and_pkexec_from_the_desktop()
    {
        Assert.Equal("sudo -n apt-get", Head(AppUpdater.DebInstallCommand("/tmp/x.deb", sudoWithoutPassword: true, terminal: false)));
        Assert.Equal("sudo apt-get", Head(AppUpdater.DebInstallCommand("/tmp/x.deb", sudoWithoutPassword: false, terminal: true)));
        Assert.Equal("pkexec apt-get", Head(AppUpdater.DebInstallCommand("/tmp/x.deb", sudoWithoutPassword: false, terminal: false)));
        // apt-get, not dpkg -i, so a new dependency comes along; a full path, which apt needs for a local file.
        var (_, args) = AppUpdater.DebInstallCommand("x.deb", false, false);
        Assert.Equal(["apt-get", "install", "-y", "--no-install-recommends", Path.GetFullPath("x.deb")], args);

        static string Head((string File, string[] Args) c) => string.Join(' ', new[] { c.File }.Concat(c.Args.TakeWhile(a => a != "install")));
    }

    [Fact]
    public void A_package_install_updates_from_the_deb_entry_of_the_manifest()
    {
        Assert.Equal("linux-x64.deb", new ReleaseStamp("OneRing", "0.1.1", "linux-x64", "o/r", null, Now, "deb").AssetKey);
        Assert.Equal("linux-x64", new ReleaseStamp("OneRing", "0.1.1", "linux-x64", "o/r", null, Now).AssetKey);
        // A stamp written before the package field existed still reads, as a folder install.
        var dir = Path.Combine(_dir, "old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ReleaseStamp.FileName),
            """{ "app": "OneRing", "version": "0.1.154", "rid": "linux-x64", "repository": "o/r", "commit": "abc", "builtAt": "2026-11-13T05:26:04Z" }""");
        var old = ReleaseStamp.Read(dir)!;
        Assert.Null(old.Package);
        Assert.Equal("linux-x64", old.AssetKey);
    }

    [Fact]
    public async Task Installing_the_next_package_runs_apt_and_checks_the_result()
    {
        var install = Path.Combine(_dir, "opt", "onering");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), Stamp("0.1.1", "deb"));
        var deb = Path.Combine(_dir, "updates", "0.1.2", "onering_0.1.2_amd64.deb");
        Directory.CreateDirectory(Path.GetDirectoryName(deb)!);
        File.WriteAllText(deb, "not really a package");
        var ran = new List<string>();
        var updater = TestApp.Updater(Path.Combine(_dir, "home"), install);
        updater.SudoWithoutPassword = () => false;
        updater.InteractiveTerminal = false;
        updater.RunElevated = (file, args) =>
        {
            ran.Add($"{file} {string.Join(' ', args)}");
            File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), Stamp("0.1.2", "deb")); // what dpkg would unpack
            return 0;
        };
        Assert.True(updater.IsDebPackage);
        Assert.Equal("linux-x64.deb", updater.AssetKey);
        var offer = new UpdateOffer(new(0, 1, 2), new(0, 1, 1), "v0.1.2", "", null, Now, "linux-x64.deb",
            new("onering_0.1.2_amd64.deb", "00", 1), new(1, "onering_0.1.2_amd64.deb", 1, "", ""));

        var r = await updater.InstallAsync(offer, deb);

        Assert.True(r.IsSuccess, r.Error);
        Assert.StartsWith("pkexec apt-get install -y", Assert.Single(ran));
        Assert.EndsWith(Path.GetFullPath(deb), ran[0]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(deb))); // the download is cleaned up
    }

    [Fact]
    public async Task A_cancelled_password_prompt_changes_nothing_and_says_so()
    {
        var install = Path.Combine(_dir, "opt", "onering");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ReleaseStamp.FileName), Stamp("0.1.1", "deb"));
        var deb = Path.Combine(_dir, "onering_0.1.2_amd64.deb");
        File.WriteAllText(deb, "x");
        var updater = TestApp.Updater(Path.Combine(_dir, "home"), install);
        updater.SudoWithoutPassword = () => false;
        updater.RunElevated = (_, _) => 126; // pkexec: dialog dismissed
        var offer = new UpdateOffer(new(0, 1, 2), new(0, 1, 1), "v0.1.2", "", null, Now, "linux-x64.deb",
            new("onering_0.1.2_amd64.deb", "00", 1), new(1, "x", 1, "", ""));

        var r = await updater.InstallAsync(offer, deb);

        Assert.False(r.IsSuccess);
        Assert.Contains("cancelled", r.Error);
        Assert.Contains("0.1.1 is still installed", r.Error);
        Assert.Contains("sudo apt install", r.Error);
        Assert.True(File.Exists(deb)); // kept, for installing by hand
    }
}

public sealed class InstallSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onering-swap-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Folder(string name, Dictionary<string, string> files)
    {
        var root = Path.Combine(_dir, name);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Replaces_files_in_place_keeps_the_rest_and_moves_the_old_ones_aside()
    {
        var install = Folder("install", new() { ["OneRing.dll"] = "old", ["ring"] = "old ring", ["mine.txt"] = "kept" });
        var staging = Folder("staging", new() { ["OneRing.dll"] = "new", ["ring"] = "new ring", ["lib/new.dll"] = "added" });

        var r = InstallSwap.Apply(staging, install, Now);

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(3, r.Value);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "OneRing.dll")));
        Assert.Equal("new ring", File.ReadAllText(Path.Combine(install, "ring")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(install, "lib", "new.dll")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(install, "mine.txt")));
        var aside = Directory.GetDirectories(Path.Combine(install, InstallSwap.OldFolder)).Single();
        Assert.Equal("old", File.ReadAllText(Path.Combine(aside, "OneRing.dll")));

        InstallSwap.CleanUp(install);
        Assert.False(Directory.Exists(Path.Combine(install, InstallSwap.OldFolder)));
    }

    [Fact]
    public void A_failure_part_way_puts_every_old_file_back()
    {
        // "lib" is a file in the install folder, so the staged lib/x.dll can't be placed; a.dll was already swapped.
        var install = Folder("install", new() { ["a.dll"] = "old a", ["lib"] = "a file, not a folder" });
        var staging = Folder("staging", new() { ["a.dll"] = "new a", ["lib/x.dll"] = "new x" });

        var r = InstallSwap.Apply(staging, install, Now);

        Assert.False(r.IsSuccess);
        Assert.Contains("Nothing was changed", r.Error);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(install, "a.dll")));
        Assert.Equal("a file, not a folder", File.ReadAllText(Path.Combine(install, "lib")));
    }

    [Fact]
    public void A_running_program_can_be_replaced_on_windows()
    {
        // What makes in-place updates possible: Windows refuses to overwrite or delete an open executable image, but
        // renames it. An open file shared for delete stands in for a mapped image here.
        var install = Folder("install", new() { ["app.dll"] = "old" });
        var staging = Folder("staging", new() { ["app.dll"] = "new" });
        using (new FileStream(Path.Combine(install, "app.dll"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var r = InstallSwap.Apply(staging, install, Now);
            Assert.True(r.IsSuccess, r.Error);
        }
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.dll")));
    }
}

public class GitHubTokenTests
{
    [Fact]
    public async Task A_saved_token_is_used_before_gh_and_is_private_to_the_user()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onering-token-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "github-token");
        var before = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Assert.Equal(new GitHubTokenFound("from-gh", "gh sign-in"), GitHubToken.Find("ONERING_TEST_NONE", () => "from-gh", file));

            Assert.True((await GitHubToken.SaveAsync(file, "  github_pat_saved \n")).IsSuccess);
            Assert.Equal(new GitHubTokenFound("github_pat_saved", "saved token"), GitHubToken.Find("ONERING_TEST_NONE", () => "from-gh", file));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

            Assert.False((await GitHubToken.SaveAsync(file, "two words")).IsSuccess);
            Assert.True(GitHubToken.Clear(file).IsSuccess);
            Assert.Equal(GitHubTokenFound.None, GitHubToken.Find("ONERING_TEST_NONE", () => null, file));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", before.Item1);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", before.Item2);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_old_gh_status_line_gives_the_token_and_a_masked_one_gives_nothing()
    {
        const string status = "github.com\n  ✓ Logged in to github.com as someone (hosts.yml)\n  ✓ Token: gho_abc123\n";
        Assert.Equal("gho_abc123", GitHubToken.ParseStatusToken(status));
        Assert.Null(GitHubToken.ParseStatusToken("github.com\n  ✓ Token: *******************\n"));
        Assert.Null(GitHubToken.ParseStatusToken("You are not logged into any GitHub hosts."));
    }

    [Fact]
    public void The_app_variable_wins_then_gh_cli()
    {
        const string name = "ONERING_TEST_GITHUB_TOKEN";
        var before = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable(name, "from-env");
            Assert.Equal("from-env", GitHubToken.Resolve(name, () => "from-gh"));
            Environment.SetEnvironmentVariable(name, null);
            Assert.Equal("from-gh", GitHubToken.Resolve(name, () => "from-gh"));
            Assert.Null(GitHubToken.Resolve(name, () => null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("GH_TOKEN", before.Item1);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", before.Item2);
        }
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}

/// <summary>A made-up app for the updater tests: data in a temp folder, releases from o/r.</summary>
internal static class TestApp
{
    public const string TokenVariable = "HOPELAB_TEST_GITHUB_TOKEN";
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public static AppUpdater Updater(string dataDirectory, string? installDirectory, GitHubReleases? releases = null) =>
        new(new AppUpdateOptions("OneRing", "o/r", dataDirectory, "OneRing") { TokenVariable = TokenVariable, InstallDirectory = installDirectory },
            new FixedClock(Now), releases);
}
