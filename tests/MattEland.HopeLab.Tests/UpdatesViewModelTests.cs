using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;
using MattEland.HopeLab.Updates.ReactiveUI;
using ReactiveUI;
using ReactiveUI.Builder;

namespace MattEland.HopeLab.Tests;

public sealed class FakeUpdates : IUpdateService
{
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public string CurrentVersion { get; set; } = "0.1.40";
    public string? NotUpdatableReason { get; set; }
    public bool AsksForPassword { get; set; }
    public string ReleasesUrl => "https://github.com/o/r/releases";
    public Result<UpdateCheck>? Next { get; set; }
    public Result<UpdateInstallResult>? InstallResult { get; set; }
    public int Checks { get; private set; }
    public string AccessSource { get; set; } = "gh sign-in";
    public List<string> SavedTokens { get; } = [];
    public bool HasSavedToken => SavedTokens.Count > 0;
    public int AccessRefreshes { get; private set; }

    public Task RefreshAccessAsync()
    {
        AccessRefreshes++;
        return Task.CompletedTask;
    }

    public static UpdateOffer Offer(string version = "0.1.41") => new(ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.40"),
        $"v{version}", $"https://github.com/o/r/releases/tag/v{version}", "notes", Now, "win-x64",
        new ReleaseAsset($"Demo-{version}-win-x64.zip", "00", 80_000_000), new GitHubReleaseAsset(1, "x", 1, "", ""));

    public static Result<UpdateCheck> Found(string version = "0.1.41") =>
        Result<UpdateCheck>.Ok(new UpdateCheck(ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.40"), Offer(version), Now));

    public static Result<UpdateCheck> UpToDate() =>
        Result<UpdateCheck>.Ok(new UpdateCheck(ReleaseVersion.Parse("0.1.40"), ReleaseVersion.Parse("0.1.40"), null, Now));

    public Task<Result<UpdateCheck>> CheckAsync(CancellationToken ct = default)
    {
        Checks++;
        return Task.FromResult(Next ?? UpToDate());
    }

    public Task<Result<UpdateInstallResult>> InstallAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(InstallResult ?? Result<UpdateInstallResult>.Ok(
            new UpdateInstallResult(offer.Version, 120, @"C:\Apps\Demo\Demo.exe", ServicesRestarted: true, ServiceWarning: null)));

    public Result<Unit> Launch(string executable) => Result<Unit>.Ok(Unit.Value);

    public Task<Result<string>> SaveTokenAsync(string token, CancellationToken ct = default)
    {
        SavedTokens.Add(token);
        AccessSource = "saved token";
        return Task.FromResult(Result<string>.Ok("Saved. GitHub accepted it."));
    }

    public Result<Unit> ClearToken()
    {
        AccessSource = "none";
        SavedTokens.Clear();
        return Result<Unit>.Ok(Unit.Value);
    }
}

public sealed class FakeUpdateHost : IUpdateHost
{
    public List<(string Title, string Message, UpdateToastKind Kind)> Toasts { get; } = [];
    public Dictionary<string, object?> Preferences { get; } = new();
    public List<string> Opened { get; } = [];

    public void Toast(string title, string message, UpdateToastKind kind) => Toasts.Add((title, message, kind));
    public Task OpenUrlAsync(string url) { Opened.Add(url); return Task.CompletedTask; }
    public T? GetPreference<T>(string key) => Preferences.TryGetValue(key, out var v) && v is T t ? t : default;
    public Task SetPreferenceAsync<T>(string key, T value) { Preferences[key] = value; return Task.CompletedTask; }
}

public sealed class UpdatesViewModelTests
{
    private static readonly Lazy<bool> ReactiveUiReady = new(() =>
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        return true;
    });

    private static (UpdatesViewModel Vm, FakeUpdates Updates, FakeUpdateHost Host) Make(Action<FakeUpdates>? setUp = null,
        Func<string?>? restartWarning = null)
    {
        _ = ReactiveUiReady.Value;
        var updates = new FakeUpdates();
        setUp?.Invoke(updates);
        var host = new FakeUpdateHost();
        var vm = new UpdatesViewModel(updates, host, "Demo")
        {
            InstallNote = ", restarts its helper",
            InstallHint = "Install it from the banner, Settings, or the tray menu.",
            RestartWarning = restartWarning,
        };
        return (vm, updates, host);
    }

    [Fact]
    public async Task A_new_release_shows_the_banner_and_toasts_once()
    {
        var (vm, updates, host) = Make(u => u.Next = FakeUpdates.Found());

        await vm.CheckAsync(userAsked: false);
        await vm.CheckAsync(userAsked: false);

        Assert.True(vm.ShowBanner);
        Assert.Equal("Demo 0.1.41 is available", vm.BannerTitle);
        Assert.Equal("You have 0.1.40. Installing downloads 76.3 MB, restarts its helper and reopens Demo.", vm.BannerBody);
        Assert.Equal("Install Demo 0.1.41", vm.TrayText);
        Assert.Single(host.Toasts, t => t.Title == "Demo 0.1.41 is available" && t.Message.Contains("tray menu"));
        Assert.Equal(2, updates.Checks);
    }

    [Fact]
    public async Task Not_now_hides_the_banner_for_that_version_only_and_remembers_it()
    {
        var (vm, updates, host) = Make(u => u.Next = FakeUpdates.Found("0.1.41"));
        await vm.CheckAsync(userAsked: false);

        await vm.DismissCommand.Execute();
        Assert.False(vm.ShowBanner);
        Assert.True(vm.HasUpdate); // still in Settings and the tray
        Assert.Equal("0.1.41", host.GetPreference<string>(UpdatesViewModel.DismissedKey));

        updates.Next = FakeUpdates.Found("0.1.42");
        await vm.CheckAsync(userAsked: false);
        Assert.True(vm.ShowBanner);
    }

    [Fact]
    public async Task A_package_install_warns_about_the_password_and_a_restart_warning_is_shown()
    {
        var (vm, _, _) = Make(u => { u.Next = FakeUpdates.Found(); u.AsksForPassword = true; }, () => "2 sessions will close.");
        await vm.CheckAsync(userAsked: false);

        Assert.Contains("asked for your password", vm.BannerBody);
        Assert.EndsWith("2 sessions will close.", vm.BannerBody);
    }

    [Fact]
    public async Task A_timed_check_that_fails_is_quiet_but_one_the_user_asked_for_is_not()
    {
        var (vm, updates, host) = Make(u => u.Next = Result<UpdateCheck>.Fail("offline"));

        await vm.CheckAsync(userAsked: false);
        Assert.Empty(host.Toasts);
        Assert.Equal("offline", vm.Error);

        await vm.CheckCommand.Execute();
        Assert.Single(host.Toasts, t => t.Kind == UpdateToastKind.Warning);

        updates.Next = FakeUpdates.UpToDate();
        await vm.CheckCommand.Execute();
        Assert.Null(vm.Error);
        Assert.Contains(host.Toasts, t => t.Title == "Demo is up to date");
        Assert.StartsWith("Up to date.", vm.StatusText);
    }

    [Fact]
    public async Task Without_any_github_access_a_timed_check_says_so_once_and_a_saved_token_fixes_it()
    {
        var (vm, updates, host) = Make(u =>
        {
            u.AccessSource = "none";
            u.Next = Result<UpdateCheck>.Fail("No releases visible for o/r.");
        });

        await vm.CheckAsync(userAsked: false);
        await vm.CheckAsync(userAsked: false);
        Assert.Single(host.Toasts, t => t.Title == "Demo can't check for updates");
        Assert.Contains("paste a token", vm.AccessText);

        updates.Next = FakeUpdates.Found();
        vm.TokenInput = "github_pat_x";
        await vm.SaveTokenCommand.Execute();

        Assert.Equal(["github_pat_x"], updates.SavedTokens);
        Assert.Null(vm.TokenInput); // never kept in the box
        Assert.True(vm.HasSavedToken);
        Assert.True(vm.HasUpdate); // saving checks straight away

        await vm.ClearTokenCommand.Execute();
        Assert.False(vm.HasSavedToken);
    }

    [Fact]
    public async Task Installing_asks_the_app_to_restart_into_the_new_copy()
    {
        var (vm, _, _) = Make(u => u.Next = FakeUpdates.Found());
        string? restartWith = null;
        vm.RestartRequested += (_, exe) => restartWith = exe;
        await vm.CheckAsync(userAsked: false);

        await vm.InstallCommand.Execute();

        Assert.Equal(@"C:\Apps\Demo\Demo.exe", restartWith);
        Assert.Contains("Restarting", vm.StatusText);
    }

    [Fact]
    public async Task The_tray_installs_directly_unless_restarting_would_interrupt_something()
    {
        string? warning = "1 session will close.";
        var (vm, _, host) = Make(u => u.Next = FakeUpdates.Found(), () => warning);
        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.CheckAsync(userAsked: false);
        await vm.DismissCommand.Execute();

        await vm.InstallFromTrayAsync();
        Assert.Equal(0, restarted);
        Assert.True(vm.ShowBanner); // back, with the warning, for the person to decide
        Assert.Contains(host.Toasts, t => t.Kind == UpdateToastKind.Warning && t.Message.StartsWith("1 session will close."));

        warning = null;
        await vm.InstallFromTrayAsync();
        Assert.Equal(1, restarted);
    }

    [Fact]
    public async Task A_failed_install_is_reported_and_does_not_restart()
    {
        var (vm, _, host) = Make(u =>
        {
            u.Next = FakeUpdates.Found();
            u.InstallResult = Result<UpdateInstallResult>.Fail("disk full");
        });
        var restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;
        await vm.CheckAsync(userAsked: false);

        await vm.InstallCommand.Execute();

        Assert.False(restarted);
        Assert.Contains(host.Toasts, t => t.Kind == UpdateToastKind.Error && t.Message == "disk full");
        Assert.True(vm.HasUpdate);
    }

    [Fact]
    public void A_source_build_never_checks_on_a_timer_and_says_why()
    {
        var (vm, updates, _) = Make(u => u.NotUpdatableReason = "This copy was built from source.");
        var scheduler = new HistoricalScheduler();

        vm.Start(scheduler);
        scheduler.AdvanceBy(TimeSpan.FromHours(10));

        Assert.Equal(0, updates.Checks);
        Assert.True(vm.HasNotUpdatableReason);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public void Timed_checks_follow_the_setting()
    {
        var (vm, updates, host) = Make();
        var scheduler = new HistoricalScheduler();
        vm.Start(scheduler);

        scheduler.AdvanceBy(UpdatesViewModel.FirstCheckDelay);
        Assert.Equal(1, updates.Checks);
        scheduler.AdvanceBy(UpdatesViewModel.CheckInterval);
        Assert.Equal(2, updates.Checks);

        vm.AutoCheck = false;
        Assert.Equal(false, host.Preferences[UpdatesViewModel.AutoCheckKey]);
        scheduler.AdvanceBy(UpdatesViewModel.CheckInterval * 3);
        Assert.Equal(2, updates.Checks);
    }
}
