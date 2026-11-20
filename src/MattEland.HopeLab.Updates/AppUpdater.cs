using System.Diagnostics;
using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>Restarts what <see cref="StopServicesAsync"/> stopped. Its error becomes <see cref="UpdateInstallResult.ServiceWarning"/>.</summary>
public delegate Task<Result<Unit>> RestartServicesAsync(CancellationToken ct);

/// <summary>
/// Stops what the app runs beside its main executable (OneRing's ring-host, say) so its files can be replaced, and
/// returns how to start it again — or null when nothing was running. Called just before files are replaced, and the
/// restart just after, whether or not the install worked.
/// </summary>
public delegate Task<RestartServicesAsync?> StopServicesAsync(string installDirectory, CancellationToken ct);

/// <summary>What an app tells <see cref="AppUpdater"/> about itself.</summary>
/// <param name="AppName">Shown in messages ("OneRing 0.1.5 is still installed").</param>
/// <param name="Repository"><c>owner/name</c> of the GitHub repository the releases come from.</param>
/// <param name="DataDirectory">The app's per-user folder (<c>~/.onering</c>): downloads go in <c>updates/</c>, a saved
/// GitHub token in <c>github-token</c>.</param>
/// <param name="DesktopExecutable">The app's main executable, without <c>.exe</c>, relaunched after an update.</param>
public sealed record AppUpdateOptions(string AppName, string Repository, string DataDirectory, string DesktopExecutable)
{
    /// <summary>An app-specific environment variable for a GitHub token, checked before GH_TOKEN and GITHUB_TOKEN.</summary>
    public string? TokenVariable { get; init; }

    /// <summary>The Inno Setup AppId of the app's Windows setup, so an in-place update can move Apps &amp; features along.</summary>
    public string? WindowsSetupAppId { get; init; }

    /// <summary>The folder being updated. Defaults to the running executable's own folder.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>The running version when there is no release stamp (a build from source). Defaults to the entry assembly's.</summary>
    public string? RunningVersion { get; init; }

    public StopServicesAsync? StopServices { get; init; }

    /// <summary>The GitHub CLI's sign-in, as a last resort for a token. Defaults to running <c>gh</c>; tests replace it.</summary>
    public Func<string?>? GitHubCliToken { get; init; }

    /// <summary>Where a saved token lives. Defaults to <see cref="TokenFile"/>, user-only.</summary>
    public ITokenStore? TokenStore { get; init; }

    public string TokenFile => Path.Combine(DataDirectory, "github-token");
    public ITokenStore Tokens => TokenStore ?? new FileTokenStore(TokenFile);
    public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");
}

/// <summary>What an install did, for the caller to report and act on.</summary>
/// <param name="DesktopExecutable">The (new) app, for the desktop to relaunch.</param>
/// <param name="ServicesRestarted">Something was running beside the app and has been started again from the new files.</param>
public sealed record UpdateInstallResult(ReleaseVersion Version, int Files, string DesktopExecutable, bool ServicesRestarted, string? ServiceWarning);

/// <summary>
/// Updates an installed copy of an app from its GitHub releases, in the folder it is installed in:
/// <list type="number">
/// <item>check <c>latest.json</c> on the latest release against the running version;</item>
/// <item>download the asset for this copy into <c>&lt;data&gt;/updates/&lt;version&gt;/</c> and check its SHA-256;</item>
/// <item>stop the app's services, install, and start them again: a folder install (archive, install script, per-user
/// setup) has its files swapped in place (<see cref="InstallSwap"/>); a Debian package install runs
/// <c>apt-get install</c> on the next <c>.deb</c>, which asks for a password.</item>
/// </list>
/// The install folder never moves, so anything that holds an absolute path into it keeps working. Only a copy with a
/// <see cref="ReleaseStamp"/> (written by the release build) updates itself; a build from source says why not.
/// </summary>
public sealed class AppUpdater
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly AppUpdateOptions _options;
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private readonly GitHubReleases _releases;

    /// <param name="http">For tests; defaults to a shared client.</param>
    public AppUpdater(AppUpdateOptions options, IClock clock, HttpClient? http = null)
    {
        _options = options;
        _clock = clock;
        _http = http ?? SharedHttp;
        InstallDirectory = Path.GetFullPath(options.InstallDirectory ?? OwnDirectory);
        Stamp = ReleaseStamp.Read(InstallDirectory);
        _releases = Releases(() => FindToken().Token);
    }

    private GitHubReleases Releases(Func<string?> token) =>
        new(_http, _options.Repository, token, $"{_options.AppName.Replace(' ', '-')}/{CurrentVersion}");

    /// <summary>The folder of the running executable (not the working directory).</summary>
    public static string OwnDirectory =>
        (Environment.ProcessPath is { Length: > 0 } p ? Path.GetDirectoryName(p) : null) ?? AppContext.BaseDirectory;

    public AppUpdateOptions Options => _options;
    public string InstallDirectory { get; }

    /// <summary>The release this copy was built as, or null for a build from source.</summary>
    public ReleaseStamp? Stamp { get; }

    public ReleaseVersion CurrentVersion =>
        ReleaseVersion.TryParse(Stamp?.Version ?? _options.RunningVersion ?? EntryVersion(), out var v) ? v.Value : new ReleaseVersion(0, 0, 0);

    public string? Rid => Stamp?.Rid ?? UpdateChecker.CurrentRid();
    public string ReleasesUrl => $"https://github.com/{_options.Repository}/releases";

    /// <summary>Why this copy can't update itself, or null when it can.</summary>
    public string? NotUpdatableReason =>
        Stamp is null ? "This copy was built from source, so it doesn't update itself. Pull and rebuild instead, or install a release."
        : Rid is null ? $"There are no {_options.AppName} releases for this operating system yet."
        : null;

    public bool CanUpdate => NotUpdatableReason is null;

    /// <summary>Installed from the Debian package: updated by installing the next package, as root.</summary>
    public bool IsDebPackage => Stamp?.Package == ReleaseStamp.DebPackage;

    /// <summary>The <c>latest.json</c> key this copy updates from (<c>linux-x64</c>, or <c>linux-x64.deb</c> for the package).</summary>
    public string? AssetKey => Stamp?.AssetKey ?? Rid;

    public string DesktopExecutablePath =>
        Path.Combine(InstallDirectory, _options.DesktopExecutable + (OperatingSystem.IsWindows() ? ".exe" : ""));

    // --- GitHub access ---

    /// <summary>
    /// The token requests would use, and where it comes from. This may run the GitHub CLI (up to a few seconds), so a UI
    /// calls it off the UI thread (<see cref="AppUpdateService"/> does).
    /// </summary>
    public GitHubTokenFound FindToken() => GitHubToken.Find(_options.TokenVariable, _options.GitHubCliToken, _options.Tokens);

    /// <summary>Whether a token is saved on this machine, whichever source is used first.</summary>
    public bool HasSavedToken => _options.Tokens.Read() is not null;

    /// <summary>
    /// Checks the token itself against GitHub (not whatever token currently takes precedence), and saves it only if it
    /// can read the repository's releases. A refused token never replaces a saved one. The message says what GitHub
    /// made of it.
    /// </summary>
    public async Task<Result<string>> SaveTokenAsync(string token, CancellationToken ct = default)
    {
        var candidate = token.Trim();
        if (!GitHubToken.LooksLikeToken(candidate)) return Result<string>.Fail("That doesn't look like a GitHub token.");
        var probe = Releases(() => candidate);

        string message;
        var latest = await probe.LatestAsync(ct).ConfigureAwait(false);
        if (latest.IsSuccess) message = $"GitHub accepted it; the latest release is {latest.Value!.Tag}.";
        else
        {
            // Latest answers 404 both for "no release yet" and "can't read releases"; listing them tells the two apart.
            var access = await probe.CanReadReleasesAsync(ct).ConfigureAwait(false);
            if (access is { IsSuccess: true, Value: true }) message = $"GitHub accepted it ({_options.Repository} has no published release yet).";
            else if (access is { IsSuccess: true, Value: false })
                return Result<string>.Fail($"Not saved: this token can't read {_options.Repository}'s releases. Give it read-only Contents access to that repository.");
            else if (access.Error!.Contains("rejected", StringComparison.Ordinal)) return Result<string>.Fail("Not saved: " + access.Error);
            else message = $"It couldn't be checked now ({access.Error}), so it was saved as it is."; // offline: keep it
        }

        var saved = await _options.Tokens.SaveAsync(candidate).ConfigureAwait(false);
        if (!saved.IsSuccess) return Result<string>.Fail(saved.Error!);
        var first = await Task.Run(FindToken, ct).ConfigureAwait(false);
        return Result<string>.Ok($"Saved. {message}" + (first.Source == "saved token" ? "" : $" {first.Source} is set too and is used first, though."));
    }

    public Result<Unit> ClearToken() => _options.Tokens.Clear();

    // --- check, download, install ---

    /// <summary>Asks GitHub for the latest release. Works from a source build too, to say what's out there.</summary>
    public Task<Result<UpdateCheck>> CheckAsync(CancellationToken ct = default)
    {
        if (AssetKey is not { } key) return Task.FromResult(Result<UpdateCheck>.Fail(NotUpdatableReason!));
        return new UpdateChecker(_releases, _clock).CheckAsync(CurrentVersion, key, ct);
    }

    /// <summary>
    /// Downloads and verifies <paramref name="offer"/>: unpacked into a staging folder for a folder install, or just the
    /// <c>.deb</c> for a package install. Returns what <see cref="InstallAsync"/> takes.
    /// </summary>
    public Task<Result<string>> DownloadAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default) =>
        !CanUpdate ? Task.FromResult(Result<string>.Fail(NotUpdatableReason!))
        : IsDebPackage ? UpdatePackage.DownloadVerifiedAsync(_releases, offer, _options.UpdatesDirectory, progress, ct)
        : UpdatePackage.PrepareAsync(_releases, offer, _options.UpdatesDirectory, progress, ct);

    /// <summary>
    /// Downloads, verifies and installs <paramref name="offer"/> while holding <see cref="TryLock"/>, so two processes (the
    /// app and its CLI, say) never update the same copy, or use the same download folder, at once. The way an app should
    /// update.
    /// </summary>
    public async Task<Result<UpdateInstallResult>> UpdateAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var held = TryLock();
        if (held is null)
            return Result<UpdateInstallResult>.Fail($"Another {_options.AppName} update is already running on this machine. Try again when it has finished.");
        var downloaded = await DownloadAsync(offer, progress, ct).ConfigureAwait(false);
        if (!downloaded.IsSuccess) return Result<UpdateInstallResult>.Fail(downloaded.Error!);
        return await InstallAsync(offer, downloaded.Value!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The machine-wide update lock (a file in the updates folder, held open exclusively). Null when another process
    /// holds it. <see cref="UpdateAsync"/> takes it; callers of <see cref="DownloadAsync"/> and <see cref="InstallAsync"/>
    /// should too.
    /// </summary>
    public IDisposable? TryLock()
    {
        try
        {
            Directory.CreateDirectory(_options.UpdatesDirectory);
            // The file is left in place, not deleted on close. On Linux and macOS the lock is an flock on the open file, and
            // .NET opens then locks in two steps: a process that opened the old file just before it was deleted could lock
            // that orphan while the next one creates and locks a new file under the same name, and both would "hold" it.
            return new FileStream(Path.Combine(_options.UpdatesDirectory, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Stops the app's services, installs the download and starts them again. The caller (the desktop app, say) still
    /// runs the old code until it restarts. Cancellation is honoured only before anything is stopped: from then on the
    /// install runs to the end, so whatever was stopped is always started again.
    /// </summary>
    public async Task<Result<UpdateInstallResult>> InstallAsync(UpdateOffer offer, string downloaded, CancellationToken ct = default)
    {
        if (!CanUpdate) return Result<UpdateInstallResult>.Fail(NotUpdatableReason!);
        if (ct.IsCancellationRequested) return Result<UpdateInstallResult>.Fail("Cancelled; nothing was changed.");
        if (IsDebPackage) return await InstallDebAsync(offer, downloaded).ConfigureAwait(false);
        if (ReleaseStamp.Read(downloaded) is not { } stamp || !string.Equals(stamp.Rid, Rid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(stamp.App, Stamp!.App, StringComparison.OrdinalIgnoreCase))
            return Result<UpdateInstallResult>.Fail($"The download isn't a {_options.AppName} build for {Rid}; nothing was changed.");
        if (!ReleaseVersion.TryParse(stamp.Version, out var staged) || staged.Value != offer.Version)
            return Result<UpdateInstallResult>.Fail($"The download is {_options.AppName} {stamp.Version}, not {offer.Version}; nothing was changed.");

        var restart = await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Result<int> swapped;
        string? warning = null;
        try
        {
            swapped = InstallSwap.Apply(downloaded, InstallDirectory, _clock.UtcNow);
            if (swapped.IsSuccess && _options.WindowsSetupAppId is { } appId)
                WindowsSetupEntry.SetVersion(appId, InstallDirectory, offer.Version.ToString());
        }
        finally
        {
            // Whether the swap worked, failed or threw, what was running comes back (new files, or the restored old ones).
            warning = await RestartAsync(restart, CancellationToken.None).ConfigureAwait(false);
        }
        if (!swapped.IsSuccess) return Result<UpdateInstallResult>.Fail(swapped.Error!);

        DeleteOwnDownload(offer, downloaded);
        return Result<UpdateInstallResult>.Ok(new UpdateInstallResult(offer.Version, swapped.Value, DesktopExecutablePath,
            restart is not null && warning is null, warning));
    }

    /// <summary>
    /// Removes what the last update moved aside. Safe to call on every start: it takes the update lock, so it never
    /// deletes the backups of an update another process is in the middle of (it skips, and the next start cleans up).
    /// </summary>
    public void CleanUp()
    {
        if (Stamp is null) return;
        using var held = TryLock();
        if (held is null) return;
        InstallSwap.CleanUp(InstallDirectory);
    }

    /// <summary>
    /// Deletes the download folder for <paramref name="offer"/> (<c>&lt;updates&gt;/&lt;version&gt;/</c>) if
    /// <paramref name="downloaded"/> is inside it; a file or folder from anywhere else is the caller's and is left alone.
    /// </summary>
    private void DeleteOwnDownload(UpdateOffer offer, string downloaded)
    {
        var own = Path.GetFullPath(Path.Combine(_options.UpdatesDirectory, offer.Version.ToString()));
        var full = Path.GetFullPath(downloaded);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(own + Path.DirectorySeparatorChar, comparison)) TryDelete(own);
    }

    private async Task<RestartServicesAsync?> StopAsync(CancellationToken ct) =>
        _options.StopServices is { } stop ? await stop(InstallDirectory, ct).ConfigureAwait(false) : null;

    private static async Task<string?> RestartAsync(RestartServicesAsync? restart, CancellationToken ct)
    {
        if (restart is null) return null;
        var r = await restart(ct).ConfigureAwait(false);
        return r.IsSuccess ? null : r.Error;
    }

    // --- Debian package ---

    /// <summary>
    /// Runs a command that may ask for a password, with this process's terminal (sudo prompts there) or none (pkexec
    /// shows the desktop's own dialog). Returns the exit code; 127 when it couldn't be started. Replaced in tests.
    /// </summary>
    public Func<string, string[], int> RunElevated { get; set; } = DefaultRunElevated;

    /// <summary>True when the caller has a terminal a sudo password prompt can use (a CLI), false for a desktop app.</summary>
    public bool InteractiveTerminal { get; set; }

    /// <summary>Whether sudo works without asking (a cached password, NOPASSWD, a CI runner). Replaced in tests.</summary>
    public Func<bool> SudoWithoutPassword { get; set; } = () => DefaultRunQuiet("sudo", "-n", "true") == 0;

    /// <summary>
    /// How the <c>.deb</c> is installed: <c>sudo -n</c> when that needs no password; <c>sudo</c> at a terminal; otherwise
    /// <c>pkexec</c>, which asks through the desktop's authentication dialog. apt-get (not dpkg) so new dependencies come too.
    /// </summary>
    public static (string File, string[] Args) DebInstallCommand(string deb, bool sudoWithoutPassword, bool terminal)
    {
        string[] apt = ["apt-get", "install", "-y", "--no-install-recommends", Path.GetFullPath(deb)];
        return sudoWithoutPassword ? ("sudo", ["-n", .. apt])
            : terminal ? ("sudo", apt)
            : ("pkexec", apt);
    }

    private async Task<Result<UpdateInstallResult>> InstallDebAsync(UpdateOffer offer, string deb)
    {
        if (!deb.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) || !File.Exists(deb))
            return Result<UpdateInstallResult>.Fail($"Expected a downloaded .deb, got {deb}; nothing was changed.");

        // Not cancellable from here: what is stopped must come back.
        var (file, args) = DebInstallCommand(deb, SudoWithoutPassword(), InteractiveTerminal);
        var restart = await StopAsync(CancellationToken.None).ConfigureAwait(false);
        int exit;
        string? warning;
        try
        {
            exit = await Task.Run(() => RunElevated(file, args)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Whatever went wrong, what was stopped comes back before the error is reported.
            await RestartAsync(restart, CancellationToken.None).ConfigureAwait(false);
            return Result<UpdateInstallResult>.Fail($"Couldn't run {file}: {e.Message} {_options.AppName} {CurrentVersion} is still installed.");
        }
        warning = await RestartAsync(restart, CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
        {
            // Only a refused or failed authentication means apt-get never ran. Any other failure can come after dpkg
            // started replacing files, so it may have left a partial install: say so, and how to finish it.
            var notRun = exit is 126 or 127 && file == "pkexec" || exit == 127;
            var why = exit switch
            {
                126 when file == "pkexec" => "The password prompt was cancelled.",
                127 when file == "pkexec" => "Authentication failed, or pkexec isn't installed (sudo apt install pkexec).",
                127 => $"{file} couldn't be started.",
                _ => $"{file} apt-get install exited with {exit}.",
            };
            return Result<UpdateInstallResult>.Fail(notRun
                ? $"{why} {_options.AppName} {CurrentVersion} is still installed. Or install it yourself: sudo apt install {Path.GetFullPath(deb)}"
                : $"{why} The install may be incomplete. Finish it with: sudo apt install {Path.GetFullPath(deb)} (or sudo dpkg --configure -a), then reopen {_options.AppName}.");
        }

        // The package replaced release.json too; make sure it is the version we meant.
        if (ReleaseStamp.Read(InstallDirectory) is not { } now || now.Version != offer.Version.ToString())
            return Result<UpdateInstallResult>.Fail($"apt-get finished, but {InstallDirectory} doesn't hold {_options.AppName} {offer.Version}.");
        DeleteOwnDownload(offer, deb);
        return Result<UpdateInstallResult>.Ok(new UpdateInstallResult(offer.Version, 1, DesktopExecutablePath,
            restart is not null && warning is null, warning));
    }

    private static int DefaultRunElevated(string file, string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            if (p is null) return 127;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 127;
        }
    }

    private static int DefaultRunQuiet(string file, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            });
            if (p is null) return 127;
            p.StandardInput.Close();
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(5))) { try { p.Kill(); } catch (InvalidOperationException) { } return 124; }
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 127;
        }
    }

    private static string EntryVersion()
    {
        var a = System.Reflection.Assembly.GetEntryAssembly();
        var info = a is null ? null : System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(a)?.InformationalVersion;
        return info is { Length: > 0 } ? info.Split('+')[0] : a?.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Processes running from an install folder: for a <see cref="StopServicesAsync"/> to wait on and, at a deadline, stop.</summary>
public static class InstalledProcesses
{
    /// <summary>Running processes named <paramref name="name"/> (no <c>.exe</c>) whose executable is in <paramref name="directory"/>.</summary>
    public static List<Process> Find(string name, string directory)
    {
        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var mine = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            if (path is not null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), dir, comparison)) mine.Add(p);
            else p.Dispose();
        }
        return mine;
    }

    /// <summary>Waits for them to exit, killing what is left at the deadline. True if there were any.</summary>
    public static async Task<bool> WaitForExitAsync(string name, string directory, TimeSpan timeout, CancellationToken ct = default)
    {
        var any = false;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var running = Find(name, directory);
            if (running.Count == 0) return any;
            any = true;
            if (DateTime.UtcNow >= deadline)
            {
                foreach (var p in running)
                {
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { p.Dispose(); }
                }
                return true;
            }
            foreach (var p in running) p.Dispose();
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }
}
