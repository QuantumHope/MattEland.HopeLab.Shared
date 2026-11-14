using System.Diagnostics;
using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>A token and where it came from (for Settings to say), or no token: go anonymous.</summary>
public sealed record GitHubTokenFound(string? Token, string Source)
{
    public static readonly GitHubTokenFound None = new(null, "none");
    public bool HasToken => Token is not null;
}

/// <summary>
/// Finds a GitHub token for reading releases of a private repository, in order:
/// <list type="number">
/// <item>an environment variable (<c>appVariable</c>, then <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>);</item>
/// <item>a token saved on this machine (<c>tokenFile</c>, written by <see cref="Save"/>: an installer, or the app's
/// settings), for a machine where the GitHub CLI can't be used;</item>
/// <item>the GitHub CLI's own sign-in.</item>
/// </list>
/// No token means "go anonymous", which is all a public repository needs.
/// </summary>
public static class GitHubToken
{
    public static string? Resolve(string? appVariable = null, Func<string?>? ghAuthToken = null, string? tokenFile = null) =>
        Find(appVariable, ghAuthToken, tokenFile).Token;

    public static GitHubTokenFound Find(string? appVariable = null, Func<string?>? ghAuthToken = null, string? tokenFile = null)
    {
        foreach (var name in new[] { appVariable, "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) return new(v.Trim(), name);
        }
        if (tokenFile is not null && ReadSaved(tokenFile) is { } saved) return new(saved, "saved token");
        return (ghAuthToken ?? FromGhCli)() is { } gh ? new(gh, "gh sign-in") : GitHubTokenFound.None;
    }

    /// <summary>The saved token, or null when there is none (or it can't be read).</summary>
    public static string? ReadSaved(string tokenFile)
    {
        try
        {
            return File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim() is { Length: > 0 } t ? t : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Saves a token for this machine, readable only by this user: on Linux and macOS the temporary file is created with
    /// mode 600 before any of the token is written, then renamed over the old one, so no other user can read it at any
    /// point; on Windows the profile folder's own permissions do the same. Nothing else in the app writes or logs it.
    /// </summary>
    public static async Task<Result<Unit>> SaveAsync(string tokenFile, string token)
    {
        var t = token.Trim();
        if (!LooksLikeToken(t)) return Result<Unit>.Fail("That doesn't look like a GitHub token.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(tokenFile))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(tokenFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(t + "\n").ConfigureAwait(false);
            File.Move(temp, tokenFile, overwrite: true);
            return Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"Couldn't save the token to {tokenFile}: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    internal static bool LooksLikeToken(string t) => t.Length > 0 && !t.Any(char.IsWhiteSpace);

    public static Result<Unit> Clear(string tokenFile)
    {
        try
        {
            if (File.Exists(tokenFile)) File.Delete(tokenFile);
            return Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"Couldn't delete {tokenFile}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>gh auth token</c>; on a gh older than 2.17, which lacks it, the token line of <c>gh auth status --show-token</c>.
    /// Null when gh is missing, signed out or slow.
    /// </summary>
    public static string? FromGhCli()
    {
        if (RunGh("auth", "token") is { } token && !token.Contains(' ') && !token.Contains('\n')) return token;
        return RunGh("auth", "status", "--show-token") is { } status ? ParseStatusToken(status) : null;
    }

    /// <summary>The first <c>Token: …</c> line of <c>gh auth status --show-token</c> (github.com is listed first).</summary>
    internal static string? ParseStatusToken(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            var i = line.IndexOf("Token: ", StringComparison.Ordinal);
            if (i < 0) continue;
            var token = line[(i + "Token: ".Length)..].Trim();
            if (token.Length > 0 && !token.Contains(' ') && token.Trim('*').Length > 0) return token;
        }
        return null;
    }

    /// <summary>Runs gh, returning stdout+stderr on exit code 0 (old versions print status to stderr).</summary>
    private static string? RunGh(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            var text = (output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).Trim();
            return p.ExitCode == 0 && text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
