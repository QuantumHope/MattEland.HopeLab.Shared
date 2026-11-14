using System.Diagnostics;

namespace MattEland.HopeLab.Git;

/// <summary>Structured result of a process run. Ported from Hymnal's GitCommandResult (D007).</summary>
public sealed record CommandResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut = false)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
    public string CommandText => string.Join(" ", new[] { FileName }.Concat(Arguments));

    public static CommandResult Failure(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, string stderr, int exitCode = -1)
        => new(fileName, arguments, workingDirectory, exitCode, string.Empty, stderr);
}

/// <summary>Runs a process directly, never through a shell.</summary>
public interface IProcessRunner
{
    Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t)
            timeoutCts.CancelAfter(t);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return CommandResult.Failure(fileName, arguments, workingDirectory, $"Failed to start {fileName}.");
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new CommandResult(fileName, arguments.ToArray(), workingDirectory, process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return CommandResult.Failure(fileName, arguments, workingDirectory, $"Timed out after {timeout}.") with { TimedOut = true };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.Failure(fileName, arguments, workingDirectory, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
