using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using MattEland.HopeLab.Updates;
using ReactiveUI;

namespace MattEland.HopeLab.Updates.ReactiveUI;

public enum UpdateToastKind { Information, Success, Warning, Error }

/// <summary>What the view model needs from the app around it: toasts, a browser, and somewhere to keep two preferences.</summary>
public interface IUpdateHost
{
    void Toast(string title, string message, UpdateToastKind kind);
    Task OpenUrlAsync(string url);

    /// <summary>A stored preference, or default when there is none.</summary>
    T? GetPreference<T>(string key);
    Task SetPreferenceAsync<T>(string key, T value);
}

/// <summary>
/// Release updates for a HopeLab desktop app, the same in every app: a banner, Settings → Updates and (where there is
/// one) a tray item all bind here. Checks a little after start and then every few hours (unless turned off); a new
/// version raises a toast once, the banner until it is dismissed for that version, and a tray item. Installing replaces
/// the files (or installs the next .deb), then asks the app to restart into the new copy. Views stay in each app, so
/// they follow its theme.
/// </summary>
public sealed class UpdatesViewModel : ReactiveObject, IDisposable
{
    public const string AutoCheckKey = "updates.autoCheck";
    public const string DismissedKey = "updates.dismissedVersion";
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly IUpdateService? _updates;
    private readonly IUpdateHost _host;
    private readonly CompositeDisposable _timers = new();
    private UpdateOffer? _offer;
    private string? _status;
    private string? _error;
    private bool _checking;
    private bool _installing;
    private long _downloaded;
    private DateTimeOffset? _lastChecked;
    private bool _autoCheck;
    private string? _dismissed;
    private string? _toasted;
    private bool _toldNoAccess;
    private string? _tokenInput;
    private bool _savingToken;

    /// <param name="updates">Null leaves updates out (the card hides; nothing is checked).</param>
    /// <param name="appName">As the app calls itself in messages ("OneRing 0.1.5 is available").</param>
    public UpdatesViewModel(IUpdateService? updates, IUpdateHost host, string appName)
    {
        _updates = updates;
        _host = host;
        AppName = appName;
        _autoCheck = host.GetPreference<bool?>(AutoCheckKey) ?? true;
        _dismissed = host.GetPreference<string>(DismissedKey);

        var idle = this.WhenAnyValue(x => x.Checking, x => x.Installing, (c, i) => !c && !i);
        CheckCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(userAsked: true), idle);
        InstallCommand = ReactiveCommand.CreateFromTask(InstallAsync,
            this.WhenAnyValue(x => x.Offer, x => x.Installing, x => x.Checking, (o, i, c) => o is not null && !i && !c && CanUpdate));
        DismissCommand = ReactiveCommand.CreateFromTask(DismissAsync);
        SaveTokenCommand = ReactiveCommand.CreateFromTask(SaveTokenAsync,
            this.WhenAnyValue(x => x.TokenInput, x => x.SavingToken, (t, s) => !string.IsNullOrWhiteSpace(t) && !s));
        ClearTokenCommand = ReactiveCommand.CreateFromTask(ClearTokenAsync);
        OpenReleaseNotesCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var url = Offer?.ReleaseUrl is { Length: > 0 } u ? u : _updates?.ReleasesUrl;
            if (url is not null) await _host.OpenUrlAsync(url);
        });
        // Finding GitHub access may run the GitHub CLI: in the background, then the Settings row follows.
        if (_updates is not null) _ = RefreshAccessAsync();
    }

    /// <summary>Looks for GitHub access again (off the UI thread) and updates the Settings row.</summary>
    public async Task RefreshAccessAsync()
    {
        if (_updates is null) return;
        try { await _updates.RefreshAccessAsync(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
        RaiseAccess();
    }

    /// <summary>Apps call this when what <see cref="RestartWarning"/> would say has changed (a session opened, say).</summary>
    public void RestartWarningChanged() => RaiseDerived();

    public string AppName { get; }

    /// <summary>
    /// What installing does besides replacing files, for the banner: ", restarts ring-host (…)". Starts with a comma
    /// or is empty.
    /// </summary>
    public string InstallNote { get; init; } = "";

    /// <summary>Where the toast tells people to install from. Apps with a tray say so.</summary>
    public string InstallHint { get; init; } = "Install it from the banner or in Settings.";

    /// <summary>Where the "no GitHub access" toast sends people.</summary>
    public string AccessHint { get; init; } = "Add a GitHub token in Settings → Updates.";

    /// <summary>
    /// What restarting would cost right now (open sessions, unsaved work), or null when nothing. The banner shows it, and
    /// <see cref="InstallFromTrayAsync"/> stops to show the window instead of installing.
    /// </summary>
    public Func<string?>? RestartWarning { get; init; }

    /// <summary>Raised after a successful install with the new app's path: the shell starts it and exits.</summary>
    public event EventHandler<string>? RestartRequested;

    public bool IsAvailable => _updates is not null;
    public bool CanUpdate => _updates is { NotUpdatableReason: null };
    public string? NotUpdatableReason => _updates?.NotUpdatableReason;
    public bool HasNotUpdatableReason => IsAvailable && !CanUpdate;
    public string CurrentVersion => _updates?.CurrentVersion ?? "";

    public UpdateOffer? Offer
    {
        get => _offer;
        private set
        {
            this.RaiseAndSetIfChanged(ref _offer, value);
            RaiseDerived();
        }
    }

    public bool HasUpdate => Offer is not null;
    public string LatestVersion => Offer?.Version.ToString() ?? "";

    /// <summary>The banner: an installable update that hasn't been dismissed for this version.</summary>
    public bool ShowBanner => Offer is { } o && CanUpdate && o.Version.ToString() != _dismissed;

    public string BannerTitle => Offer is { } o ? $"{AppName} {o.Version} is available" : "";

    public string BannerBody => Offer is { } o
        ? $"You have {o.Current}. Installing downloads {Size(o.Archive.Size)}{InstallNote} and reopens {AppName}."
          + (_updates?.AsksForPassword == true ? " The package is installed with apt-get, so you'll be asked for your password." : "")
          + (RestartWarning?.Invoke() is { Length: > 0 } w ? " " + w : "")
        : "";

    public string TrayText => Offer is { } o ? $"Install {AppName} {o.Version}" : "";

    public bool Checking { get => _checking; private set { this.RaiseAndSetIfChanged(ref _checking, value); RaiseDerived(); } }
    public bool Installing { get => _installing; private set { this.RaiseAndSetIfChanged(ref _installing, value); RaiseDerived(); } }

    public string? Error { get => _error; private set { this.RaiseAndSetIfChanged(ref _error, value); RaiseDerived(); } }
    public bool HasError => Error is not null;

    public DateTimeOffset? LastChecked { get => _lastChecked; private set { this.RaiseAndSetIfChanged(ref _lastChecked, value); RaiseDerived(); } }

    /// <summary>One line for Settings: what is known right now.</summary>
    public string StatusText => _status ?? (Installing ? Progress
        : Checking ? "Checking GitHub for a newer release…"
        : Offer is { } o ? $"{AppName} {o.Version} is available (you have {o.Current})."
        : LastChecked is { } t ? $"Up to date. Last checked {t.ToLocalTime():g}."
        : AutoCheck ? "Not checked yet this session." : "Automatic checks are off.");

    public string Progress => _downloaded > 0 && Offer is { } o
        ? $"Downloading {Size(_downloaded)} of {Size(o.Archive.Size)}…"
        : "Installing…";

    public string InstallLabel => Offer is { } o ? $"Install {o.Version} and restart" : "Install and restart";

    /// <summary>Check at start and every few hours. Saved per machine; a check from Settings works either way.</summary>
    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (_autoCheck == value) return;
            this.RaiseAndSetIfChanged(ref _autoCheck, value);
            RaiseDerived();
            _ = _host.SetPreferenceAsync(AutoCheckKey, value);
        }
    }

    // --- GitHub access (for private repositories) ---

    /// <summary>What Settings types into; cleared once saved, never shown back.</summary>
    public string? TokenInput { get => _tokenInput; set => this.RaiseAndSetIfChanged(ref _tokenInput, value); }
    public bool SavingToken { get => _savingToken; private set => this.RaiseAndSetIfChanged(ref _savingToken, value); }

    public string AccessSource => _updates?.AccessSource ?? "none";
    /// <summary>A token is saved on this machine (Settings offers to remove it), even if another source is used first.</summary>
    public bool HasSavedToken => _updates?.HasSavedToken == true;

    public string AccessText => AccessSource switch
    {
        "checking" => "Checking GitHub access…",
        "none" => "No GitHub access. If the repository is private, paste a token below, or sign in with the GitHub CLI (gh auth login).",
        "saved token" => "Using the token saved on this machine.",
        "gh sign-in" => "Using the GitHub CLI's sign-in. A token saved here would be used instead.",
        var variable => $"Using the {variable} environment variable.",
    };

    public ReactiveCommand<Unit, Unit> CheckCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenReleaseNotesCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveTokenCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearTokenCommand { get; }

    /// <summary>Starts the timed checks. Nothing is checked for a copy that can't install what it finds.</summary>
    public void Start(IScheduler? scheduler = null)
    {
        if (!CanUpdate) return;
        var s = scheduler ?? RxSchedulers.MainThreadScheduler;
        Observable.Timer(FirstCheckDelay, CheckInterval, s)
            .Where(_ => AutoCheck && !Checking && !Installing)
            .SelectMany(_ => Observable.FromAsync(() => CheckAsync(userAsked: false)))
            .Subscribe(_ => { }, _ => { })
            .DisposeWith(_timers);
    }

    /// <summary>
    /// Checks once. A check already running, or an install, wins: every caller (the timer, Check now, saving a token)
    /// goes through this guard, so two checks never overlap and Checking can't be cleared under another one.
    /// </summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (_updates is null || Checking || Installing) return;
        Checking = true;
        try
        {
            var r = await _updates.CheckAsync();
            if (!r.IsSuccess)
            {
                // A timed check that fails stays quiet (offline is normal); Settings shows the reason.
                Error = r.Error;
                if (userAsked) _host.Toast("Couldn't check for updates", r.Error ?? "Unknown error", UpdateToastKind.Warning);
                // Only an access failure (a private repository and no token, a rejected token) is worth a toast: being
                // offline, or a public repository's check failing for any other reason, stays quiet.
                else if (!_toldNoAccess && GitHubReleases.IsAccessProblem(r.Error))
                {
                    // But no way in at all won't fix itself: say so once per run.
                    _toldNoAccess = true;
                    _host.Toast($"{AppName} can't check for updates", $"GitHub didn't show its releases. {AccessHint}", UpdateToastKind.Warning);
                }
                return;
            }
            Error = null;
            LastChecked = r.Value!.CheckedAt;
            Offer = r.Value.Offer;
            if (Offer is { } o && CanUpdate && _toasted != o.Tag)
            {
                _toasted = o.Tag;
                _host.Toast($"{AppName} {o.Version} is available", InstallHint, UpdateToastKind.Information);
            }
            else if (Offer is null && userAsked)
                _host.Toast($"{AppName} is up to date", $"{CurrentVersion} is the latest release.", UpdateToastKind.Success);
        }
        finally { Checking = false; }
    }

    /// <summary>
    /// For a tray item: installs straight away unless <see cref="RestartWarning"/> says restarting would cost something,
    /// in which case the banner comes back with that warning and the person decides there. Show the window first.
    /// </summary>
    public async Task InstallFromTrayAsync()
    {
        if (RestartWarning?.Invoke() is { Length: > 0 } warning)
        {
            _dismissed = null;
            RaiseDerived();
            _host.Toast($"Restarting {AppName} would interrupt something", $"{warning} Use Install on the banner to go ahead.", UpdateToastKind.Warning);
            return;
        }
        await InstallCommand.Execute();
    }

    private async Task InstallAsync()
    {
        if (_updates is null || Offer is not { } offer) return;
        Installing = true;
        _downloaded = 0;
        try
        {
            var progress = new Progress<long>(n =>
            {
                _downloaded = n;
                this.RaisePropertyChanged(nameof(Progress));
                this.RaisePropertyChanged(nameof(StatusText));
            });
            var r = await _updates.InstallAsync(offer, progress);
            if (!r.IsSuccess)
            {
                Error = r.Error;
                _host.Toast("Update failed", r.Error ?? "Unknown error", UpdateToastKind.Error);
                return;
            }
            if (r.Value!.ServiceWarning is { } w) _host.Toast("Something didn't come back after the update", w, UpdateToastKind.Warning);
            _status = $"{AppName} {r.Value.Version} is installed. Restarting…";
            RaiseDerived();
            RestartRequested?.Invoke(this, r.Value.DesktopExecutable);
        }
        finally { Installing = false; }
    }

    private async Task DismissAsync()
    {
        if (Offer is not { } o) return;
        _dismissed = o.Version.ToString();
        RaiseDerived();
        await _host.SetPreferenceAsync(DismissedKey, _dismissed);
    }

    private async Task SaveTokenAsync()
    {
        if (_updates is null || TokenInput is not { } token) return;
        SavingToken = true;
        try
        {
            var r = await _updates.SaveTokenAsync(token);
            TokenInput = null;
            RaiseAccess();
            if (!r.IsSuccess) { _host.Toast("Token not saved", r.Error ?? "", UpdateToastKind.Error); return; }
            _host.Toast("Token saved", r.Value ?? "", UpdateToastKind.Success);
            Error = null;
        }
        finally { SavingToken = false; }
        if (CanUpdate) await CheckAsync(userAsked: false);
    }

    private async Task ClearTokenAsync()
    {
        if (_updates is null) return;
        var r = _updates.ClearToken();
        await _updates.RefreshAccessAsync();
        if (r.IsSuccess) _host.Toast("Token removed", "Updates fall back to the GitHub CLI's sign-in, if there is one.", UpdateToastKind.Information);
        else _host.Toast("Couldn't remove the token", r.Error ?? "", UpdateToastKind.Error);
        RaiseAccess();
    }

    private void RaiseAccess()
    {
        foreach (var n in new[] { nameof(AccessSource), nameof(HasSavedToken), nameof(AccessText) }) this.RaisePropertyChanged(n);
    }

    private void RaiseDerived()
    {
        foreach (var n in new[] { nameof(HasUpdate), nameof(LatestVersion), nameof(ShowBanner), nameof(BannerTitle), nameof(BannerBody),
                     nameof(TrayText), nameof(StatusText), nameof(Progress), nameof(InstallLabel), nameof(HasError) })
            this.RaisePropertyChanged(n);
    }

    private static string Size(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public void Dispose() => _timers.Dispose();
}
