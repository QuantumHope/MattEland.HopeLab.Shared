namespace MattEland.HopeLab.Git;

public enum FileChangeKind { Modified, Renamed, Unmerged, Untracked, Ignored }

public sealed record ChangedFile(string Path, string XY, FileChangeKind Kind, string? OriginalPath = null);

/// <summary>Parsed <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record WorktreeStatus(
    string? HeadSha,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<ChangedFile> Files)
{
    public bool IsDetached => Branch is null;
    public bool HasUpstream => Upstream is not null;
    public bool IsDirty => Files.Any(f => f.Kind != FileChangeKind.Ignored);
    public int TrackedChanges => Files.Count(f => f.Kind is FileChangeKind.Modified or FileChangeKind.Renamed or FileChangeKind.Unmerged);
    public int UntrackedFiles => Files.Count(f => f.Kind == FileChangeKind.Untracked);
    public bool HasConflicts => Files.Any(f => f.Kind == FileChangeKind.Unmerged);
}

/// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
public sealed record WorktreeEntry(
    string Path,
    string? HeadSha,
    string? Branch,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    bool IsPrunable,
    string? LockReason = null,
    string? PrunableReason = null);

public sealed record MergePreview(bool Supported, bool Clean, IReadOnlyList<string> ConflictedFiles);

public sealed record GitVersion(int Major, int Minor, int Patch) : IComparable<GitVersion>
{
    public int CompareTo(GitVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public bool AtLeast(int major, int minor) => CompareTo(new GitVersion(major, minor, 0)) >= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static GitVersion? Parse(string text)
    {
        // "git version 2.34.1", "git version 2.45.1.windows.1", "git version 2.50.0 (Apple Git-155)"
        var tokens = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(tokens, "version");
        var token = at >= 0 && at + 1 < tokens.Length ? tokens[at + 1] : tokens.FirstOrDefault(t => t.Length > 0 && char.IsDigit(t[0]));
        if (token is null) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return new GitVersion(major, minor, patch);
    }
}

public sealed class GitException(string message, CommandResult? result = null) : Exception(message)
{
    public CommandResult? Result { get; } = result;
}

/// <summary>
/// Outcome of <see cref="GitClient.RebaseAsync"/>. When git stopped on conflicts, <c>ConflictedFiles</c> lists them and
/// <c>Aborted</c> says whether <c>git rebase --abort</c> put the worktree back; <c>AbortError</c> is set when it didn't.
/// </summary>
public sealed record RebaseResult(bool Success, bool Aborted, IReadOnlyList<string> ConflictedFiles, CommandResult Command, string? AbortError = null)
{
    public bool StoppedOnConflict => ConflictedFiles.Count > 0 || Aborted;
}
