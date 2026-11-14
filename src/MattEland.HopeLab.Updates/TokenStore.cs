using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>
/// Where an app keeps the GitHub token it updates with, for a machine without the GitHub CLI. The default is a
/// user-only file in the app's data folder (<see cref="FileTokenStore"/>); an app with its own secret storage (Hymnal's
/// <c>ICredentialStore</c>, say) passes its own through <see cref="AppUpdateOptions.TokenStore"/>.
/// </summary>
public interface ITokenStore
{
    /// <summary>The saved token, or null when there is none (or it can't be read).</summary>
    string? Read();

    Task<Result<Unit>> SaveAsync(string token);

    Result<Unit> Clear();

    /// <summary>Where it is kept, for Settings and messages: "~/.onering/github-token", "Windows Credential Manager".</summary>
    string Description { get; }
}

/// <summary>A token in a file readable only by this user (see <see cref="GitHubToken.SaveAsync"/>).</summary>
public sealed class FileTokenStore(string path) : ITokenStore
{
    public string Path { get; } = path;
    public string? Read() => GitHubToken.ReadSaved(Path);
    public Task<Result<Unit>> SaveAsync(string token) => GitHubToken.SaveAsync(Path, token);
    public Result<Unit> Clear() => GitHubToken.Clear(Path);
    public string Description => Path;
}
