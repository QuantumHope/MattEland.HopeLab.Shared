using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace MattEland.HopeLab.Themes.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Runs test bodies on one shared headless Avalonia session.</summary>
public static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task Run(Func<Task> body) => Session.Value.Dispatch(async () =>
    {
        await body();
        return true;
    }, CancellationToken.None);

    public static Task Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    public static void Pump() => Dispatcher.UIThread.RunJobs();
}
