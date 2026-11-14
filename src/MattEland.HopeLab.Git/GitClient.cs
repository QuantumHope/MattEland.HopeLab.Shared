using System.Collections.Concurrent;

namespace MattEland.HopeLab.Git;

/// <summary>
/// Git CLI wrapper. Reads run freely; mutating commands go through <see cref="MutateAsync"/>,
/// which serializes them per repository (claude-code #55724).
/// </summary>
public sealed class GitClient(IProcessRunner? runner = null, string gitExecutable = "git", TimeProvider? time = null)
{
    /// <summary>How long a <see cref="GetVersionAsync"/> answer is reused, so an upgraded git shows up without a restart.</summary>
    public static readonly TimeSpan VersionCacheDuration = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // One gate per repository: case-insensitive on Windows, where C:\Repos\Demo and c:\repos\demo are the same folder.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private GitVersion? _version;
    private DateTimeOffset _versionAt;

    private static readonly IReadOnlyDictionary<string, string> NonInteractive = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0", // status must not take index.lock
        ["LC_ALL"] = "C",
    };

    public Task<CommandResult> RunAsync(string? cwd, IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
        => _runner.RunAsync(gitExecutable, args, cwd, NonInteractive, timeout ?? TimeSpan.FromSeconds(60), ct);

    private async Task<string> RunOrThrowAsync(string? cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await RunAsync(cwd, args, ct: ct).ConfigureAwait(false);
        if (!r.IsSuccess)
            throw new GitException($"{r.CommandText} failed: {r.Stderr.Trim()}", r);
        return r.Stdout;
    }

    /// <summary>Runs <paramref name="action"/> while holding the per-repository mutation gate.</summary>
    public async Task<T> MutateAsync<T>(string repoRoot, Func<Task<T>> action, CancellationToken ct = default)
    {
        var gate = RepoGates.GetOrAdd(NormalizePath(repoRoot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<GitVersion?> GetVersionAsync(CancellationToken ct = default)
    {
        if (_version is not null && _time.GetUtcNow() - _versionAt < VersionCacheDuration) return _version;
        var r = await RunAsync(null, ["--version"], ct: ct).ConfigureAwait(false);
        _versionAt = _time.GetUtcNow();
        return _version = r.IsSuccess ? GitVersion.Parse(r.Stdout) : null;
    }

    /// <summary>The main worktree's root for any path inside a repo or one of its linked worktrees.</summary>
    public async Task<string?> GetMainWorktreeRootAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct: ct).ConfigureAwait(false);
        if (!r.IsSuccess) return null;
        var commonDir = r.Stdout.Trim();
        // For a normal repo the common dir is <root>/.git. Bare repos are not supported as registry roots.
        if (Path.GetFileName(commonDir.TrimEnd('/', '\\')) == ".git")
            return NormalizePath(Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'))!);
        var top = await RunAsync(path, ["rev-parse", "--show-toplevel"], ct: ct).ConfigureAwait(false);
        return top.IsSuccess ? NormalizePath(top.Stdout.Trim()) : null;
    }

    public async Task<string?> GetGitDirAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ["rev-parse", "--path-format=absolute", "--git-dir"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess ? r.Stdout.Trim() : null;
    }

    public async Task<string?> GetRemoteUrlAsync(string repoRoot, string remote = "origin", CancellationToken ct = default)
    {
        var r = await RunAsync(repoRoot, ["remote", "get-url", remote], ct: ct).ConfigureAwait(false);
        return r.IsSuccess ? r.Stdout.Trim() : null;
    }

    /// <summary>Local branches (<c>main</c>) and remote-tracking ones with their remote (<c>origin/main</c>), each sorted by name.</summary>
    public async Task<(IReadOnlyList<string> Local, IReadOnlyList<string> Remote)> ListBranchesAsync(string repoRoot, CancellationToken ct = default)
    {
        var r = await RunAsync(repoRoot, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess ? ParseBranchRefs(r.Stdout) : ([], []);
    }

    /// <summary>Parses full ref names (<c>refs/heads/x</c>, <c>refs/remotes/origin/x</c>); a remote's symbolic HEAD is left out.</summary>
    public static (IReadOnlyList<string> Local, IReadOnlyList<string> Remote) ParseBranchRefs(string forEachRefOutput)
    {
        var local = new List<string>();
        var remote = new List<string>();
        foreach (var line in forEachRefOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("refs/heads/", StringComparison.Ordinal)) local.Add(line["refs/heads/".Length..]);
            else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal) && !line.EndsWith("/HEAD", StringComparison.Ordinal))
                remote.Add(line["refs/remotes/".Length..]);
        }
        local.Sort(StringComparer.Ordinal);
        remote.Sort(StringComparer.Ordinal);
        return (local, remote);
    }

    /// <summary>origin/HEAD if set, else <c>main</c> or <c>master</c> if present, else the current branch.</summary>
    public async Task<string?> GetDefaultBranchAsync(string repoRoot, CancellationToken ct = default)
    {
        var r = await RunAsync(repoRoot, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct: ct).ConfigureAwait(false);
        if (r.IsSuccess)
        {
            var v = r.Stdout.Trim();
            return v.StartsWith("origin/", StringComparison.Ordinal) ? v["origin/".Length..] : v;
        }
        foreach (var candidate in new[] { "main", "master" })
            if (await BranchExistsAsync(repoRoot, candidate, ct).ConfigureAwait(false))
                return candidate;
        var cur = await RunAsync(repoRoot, ["branch", "--show-current"], ct: ct).ConfigureAwait(false);
        return cur.IsSuccess && cur.Stdout.Trim().Length > 0 ? cur.Stdout.Trim() : null;
    }

    public async Task<bool> BranchExistsAsync(string repoRoot, string branch, CancellationToken ct = default)
        => (await RunAsync(repoRoot, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], ct: ct).ConfigureAwait(false)).IsSuccess;

    public async Task<bool> IsValidBranchNameAsync(string name, CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('-')
           && (await RunAsync(null, ["check-ref-format", "--branch", name], ct: ct).ConfigureAwait(false)).IsSuccess;

    public async Task<WorktreeStatus> GetStatusAsync(string worktreePath, CancellationToken ct = default)
        => PorcelainV2Parser.Parse(await RunOrThrowAsync(worktreePath,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal"], ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(string repoRoot, CancellationToken ct = default)
        => WorktreeListParser.Parse(await RunOrThrowAsync(repoRoot, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false));

    /// <summary>Commits on HEAD that are on no remote-tracking branch. Works whether or not an upstream is set.</summary>
    public async Task<int> CountUnpushedCommitsAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ["rev-list", "--count", "HEAD", "--not", "--remotes"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess && int.TryParse(r.Stdout.Trim(), out var n) ? n : 0;
    }

    /// <summary>Whether <paramref name="branch"/> is fully contained in <paramref name="into"/>.</summary>
    public async Task<bool> IsMergedAsync(string repoRoot, string branch, string into, CancellationToken ct = default)
        => (await RunAsync(repoRoot, ["merge-base", "--is-ancestor", $"refs/heads/{branch}", into], ct: ct).ConfigureAwait(false)).IsSuccess;

    /// <summary>True when the branch has an upstream configured that no longer exists.</summary>
    public async Task<bool> IsUpstreamGoneAsync(string repoRoot, string branch, CancellationToken ct = default)
    {
        var r = await RunAsync(repoRoot, ["for-each-ref", "--format=%(upstream:track)", $"refs/heads/{branch}"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess && r.Stdout.Contains("[gone]", StringComparison.Ordinal);
    }

    public async Task<DateTimeOffset?> GetLastCommitTimeAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ["log", "-1", "--format=%cI"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess && DateTimeOffset.TryParse(r.Stdout.Trim(), out var t) ? t : null;
    }

    /// <summary><c>git merge-tree --write-tree --name-only</c>; needs git 2.38.</summary>
    public async Task<MergePreview> PreviewMergeAsync(string repoRoot, string branch, string into, CancellationToken ct = default)
    {
        var version = await GetVersionAsync(ct).ConfigureAwait(false);
        if (version is null || !version.AtLeast(2, 38))
            return new MergePreview(false, false, []);
        var r = await RunAsync(repoRoot, ["merge-tree", "--write-tree", "--name-only", "--no-messages", into, branch], ct: ct).ConfigureAwait(false);
        if (r.ExitCode == 0) return new MergePreview(true, true, []);
        if (r.ExitCode == 1)
        {
            var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new MergePreview(true, false, lines.Skip(1).ToArray()); // first line is the tree id
        }
        throw new GitException($"merge-tree failed: {r.Stderr.Trim()}", r);
    }

    public Task<CommandResult> AddWorktreeAsync(string repoRoot, string path, string branch, string? from, bool createBranch, bool relativePaths = false, CancellationToken ct = default)
        => MutateAsync(repoRoot, () =>
        {
            var args = new List<string> { "worktree", "add" };
            if (relativePaths) args.Add("--relative-paths");
            if (createBranch) { args.Add("-b"); args.Add(branch); args.Add(path); if (from is not null) args.Add(from); }
            else { args.Add(path); args.Add(branch); }
            return RunAsync(repoRoot, args, TimeSpan.FromMinutes(5), ct);
        }, ct);

    /// <summary><c>git worktree remove</c> then <c>prune</c>. Never deletes recursively itself (claude-code #48927).</summary>
    public Task<CommandResult> RemoveWorktreeAsync(string repoRoot, string path, bool force, CancellationToken ct = default)
        => MutateAsync(repoRoot, async () =>
        {
            var args = new List<string> { "worktree", "remove" };
            if (force) args.Add("--force");
            args.Add(path);
            var r = await RunAsync(repoRoot, args, ct: ct).ConfigureAwait(false);
            if (r.IsSuccess)
                await RunAsync(repoRoot, ["worktree", "prune"], ct: ct).ConfigureAwait(false);
            return r;
        }, ct);

    public Task<CommandResult> DeleteMergedBranchAsync(string repoRoot, string branch, CancellationToken ct = default)
        => MutateAsync(repoRoot, () => RunAsync(repoRoot, ["branch", "-d", branch], ct: ct), ct);

    /// <summary>
    /// The newest <paramref name="maxCount"/> commits on every branch, remote-tracking branch and tag (not stash), in
    /// topological order, for <see cref="GraphLayout"/>.
    /// </summary>
    public async Task<IReadOnlyList<GraphCommit>> LogGraphAsync(string repoRoot, int maxCount, CancellationToken ct = default)
        => CommitLogParser.Parse(await RunOrThrowAsync(repoRoot,
            ["log", "--exclude=refs/stash", "--all", "--topo-order", "--decorate=full", $"--max-count={maxCount}",
             $"--format={CommitLogParser.Format}"], ct).ConfigureAwait(false));

    /// <summary>The commit <paramref name="rev"/> names, or null when it names none.</summary>
    public async Task<string?> ResolveCommitAsync(string cwd, string rev, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rev) || rev.StartsWith('-')) return null;
        var r = await RunAsync(cwd, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess ? r.Stdout.Trim() : null;
    }

    /// <summary>Commits reachable from <paramref name="to"/> but not from <paramref name="from"/>.</summary>
    public async Task<int> CountCommitsAsync(string cwd, string from, string to, CancellationToken ct = default)
    {
        var r = await RunAsync(cwd, ["rev-list", "--count", $"{from}..{to}"], ct: ct).ConfigureAwait(false);
        return r.IsSuccess && int.TryParse(r.Stdout.Trim(), out var n) ? n : 0;
    }

    /// <summary>The remote a remote-tracking name belongs to (<c>origin</c> for <c>origin/main</c>), or null for a local name.</summary>
    public async Task<string?> RemoteOfAsync(string repoRoot, string name, CancellationToken ct = default)
    {
        var r = await RunAsync(repoRoot, ["remote"], ct: ct).ConfigureAwait(false);
        if (!r.IsSuccess) return null;
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(remote => name.StartsWith(remote + "/", StringComparison.Ordinal))
            .OrderByDescending(remote => remote.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// What git is in the middle of in a worktree (<c>rebase</c>, <c>merge</c>, <c>cherry-pick</c>, <c>revert</c>), or null.
    /// <paramref name="gitDir"/> is the worktree's own git dir (<see cref="GetGitDirAsync"/>).
    /// </summary>
    public static string? OperationInProgress(string gitDir)
    {
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "rebase";
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        return null;
    }

    /// <summary><c>git fetch --prune</c> from one remote, or from all of them when <paramref name="remote"/> is null.</summary>
    public Task<CommandResult> FetchAsync(string repoRoot, string? remote = null, CancellationToken ct = default)
        => MutateAsync(repoRoot, () => RunAsync(repoRoot,
            remote is null ? ["fetch", "--prune", "--all"] : ["fetch", "--prune", "--end-of-options", remote],
            TimeSpan.FromMinutes(3), ct), ct);

    /// <summary>
    /// <c>git pull --no-rebase --ff-only</c> in a worktree: brings its branch up to its upstream when that needs no merge.
    /// Explicit about both, so a <c>pull.rebase</c> setting can't turn it into a rebase.
    /// </summary>
    public Task<CommandResult> PullFastForwardAsync(string repoRoot, string worktreePath, CancellationToken ct = default)
        => MutateAsync(repoRoot, () => RunAsync(worktreePath, ["pull", "--no-rebase", "--ff-only"], TimeSpan.FromMinutes(3), ct), ct);

    /// <summary>
    /// Rebases the branch checked out in <paramref name="worktreePath"/> onto <paramref name="onto"/>. When git stops
    /// on a conflict the rebase is aborted, so the worktree is left as it was, and the conflicted files are reported.
    /// The caller checks first that the worktree is clean and not mid-operation.
    /// </summary>
    public Task<RebaseResult> RebaseAsync(string repoRoot, string worktreePath, string onto, CancellationToken ct = default)
        => MutateAsync(repoRoot, async () =>
        {
            // Checked again inside the gate: an operation that is already running belongs to someone else, so refuse
            // and leave it alone. Any rebase found in progress after this point was started by this call.
            var gitDir = await GetGitDirAsync(worktreePath, ct).ConfigureAwait(false);
            if (gitDir is not null && OperationInProgress(gitDir) is { } running)
                return new RebaseResult(false, false, [],
                    CommandResult.Failure("git", ["rebase", onto], worktreePath, $"A {running} is already in progress in this worktree."));
            ct.ThrowIfCancellationRequested();

            // Once started, a rebase is not cancelled: killing git midway would leave the worktree half-rebased. Only the
            // timeout can stop it, and the in-progress check below then aborts it.
            // core.editor=true: a rebase never waits for an editor (a reword or a commit message prompt).
            var r = await RunAsync(worktreePath, ["-c", "core.editor=true", "rebase", "--no-autostash", "--end-of-options", onto],
                TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            if (r.IsSuccess) return new RebaseResult(true, false, [], r);

            gitDir ??= await GetGitDirAsync(worktreePath, CancellationToken.None).ConfigureAwait(false);
            if (gitDir is null || OperationInProgress(gitDir) != "rebase")
                return new RebaseResult(false, false, [], r); // refused before starting; nothing to undo

            // Cleanup runs to completion whatever the caller does, bounded by its own timeouts.
            var conflicts = await RunAsync(worktreePath, ["diff", "--name-only", "--diff-filter=U"], TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            var files = conflicts.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var abort = await RunAsync(worktreePath, ["rebase", "--abort"], TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            return new RebaseResult(false, abort.IsSuccess, files, r, abort.IsSuccess ? null : abort.Stderr.Trim());
        }, ct);

    public static bool IsIndexLocked(string gitDir) => File.Exists(Path.Combine(gitDir, "index.lock"));

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
