using MattEland.HopeLab.Git;
using Xunit;

namespace MattEland.HopeLab.Tests;

public class GitVersionCacheTests
{
    private sealed class VersionRunner : IProcessRunner
    {
        public string Version { get; set; } = "2.54.0";
        public int Calls { get; private set; }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null,
            IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CommandResult(fileName, arguments, workingDirectory, 0, $"git version {Version}\n", ""));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task An_upgraded_git_is_noticed_once_the_cached_version_expires()
    {
        var runner = new VersionRunner();
        var clock = new Clock();
        var git = new GitClient(runner, time: clock);

        Assert.Equal("2.54.0", (await git.GetVersionAsync())?.ToString());
        runner.Version = "2.56.0";
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal("2.54.0", (await git.GetVersionAsync())?.ToString()); // still cached
        Assert.Equal(1, runner.Calls);

        clock.Now += GitClient.VersionCacheDuration;
        Assert.Equal("2.56.0", (await git.GetVersionAsync())?.ToString());
    }
}
