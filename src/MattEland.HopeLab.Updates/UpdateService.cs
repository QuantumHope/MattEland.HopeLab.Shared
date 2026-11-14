using System.Diagnostics;
using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>Release updates as an app's UI sees them (a fake in tests). <see cref="AppUpdateService"/> is the real one.</summary>
public interface IUpdateService
{
    /// <summary>The running version, as releases number it.</summary>
    string CurrentVersion { get; }

    /// <summary>Null when this copy can update itself; otherwise why not (a build from source, say).</summary>
    string? NotUpdatableReason { get; }

    /// <summary>Installed from the .deb: installing an update asks for the user's password (pkexec).</summary>
    bool AsksForPassword { get; }

    string ReleasesUrl { get; }

    Task<Result<UpdateCheck>> CheckAsync(CancellationToken ct = default);

    /// <summary>Downloads, verifies and installs <paramref name="offer"/>. The app keeps running the old code until it restarts.</summary>
    Task<Result<UpdateInstallResult>> InstallAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Starts the newly installed app. The caller then shuts this one down.</summary>
    Result<Unit> Launch(string executable);

    /// <summary>Where GitHub access comes from now: an environment variable's name, "saved token", "gh sign-in" or "none".</summary>
    string AccessSource { get; }

    /// <summary>Saves a token on this machine (for one without the GitHub CLI) and says what GitHub made of it.</summary>
    Task<Result<string>> SaveTokenAsync(string token, CancellationToken ct = default);

    Result<Unit> ClearToken();
}

/// <summary>
/// <see cref="IUpdateService"/> over an <see cref="AppUpdater"/>. Creating it clears what the previous update moved
/// aside (the old process has exited by now, or it is left for next time).
/// </summary>
public sealed class AppUpdateService : IUpdateService
{
    public AppUpdateService(AppUpdater updater)
    {
        Updater = updater;
        Task.Run(updater.CleanUp);
    }

    public AppUpdater Updater { get; }

    public string CurrentVersion => Updater.CurrentVersion.ToString();
    public string? NotUpdatableReason => Updater.NotUpdatableReason;
    public bool AsksForPassword => Updater.IsDebPackage;
    public string ReleasesUrl => Updater.ReleasesUrl;

    public Task<Result<UpdateCheck>> CheckAsync(CancellationToken ct = default) => Updater.CheckAsync(ct);

    public string AccessSource => Updater.FindToken().Source;
    public Task<Result<string>> SaveTokenAsync(string token, CancellationToken ct = default) => Updater.SaveTokenAsync(token, ct);
    public Result<Unit> ClearToken() => Updater.ClearToken();

    public async Task<Result<UpdateInstallResult>> InstallAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var downloaded = await Updater.DownloadAsync(offer, progress, ct).ConfigureAwait(false);
        if (!downloaded.IsSuccess) return Result<UpdateInstallResult>.Fail(downloaded.Error!);
        return await Updater.InstallAsync(offer, downloaded.Value!, ct).ConfigureAwait(false);
    }

    public Result<Unit> Launch(string executable)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            });
            return p is null ? Result<Unit>.Fail($"Couldn't start {executable}.") : Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Result<Unit>.Fail($"Couldn't start {executable}: {e.Message}");
        }
    }
}
