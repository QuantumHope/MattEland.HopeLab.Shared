namespace MattEland.HopeLab.Git;

public static class PorcelainV2Parser
{
    /// <summary>Parses NUL-separated output of <c>git status --porcelain=v2 --branch -z</c>.</summary>
    public static WorktreeStatus Parse(string stdout)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new List<ChangedFile>();
        var records = stdout.Split('\0');

        for (var i = 0; i < records.Length; i++)
        {
            var rec = records[i];
            if (rec.Length == 0) continue;

            if (rec.StartsWith("# ", StringComparison.Ordinal))
            {
                var header = rec[2..];
                if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
                {
                    var v = header["branch.oid ".Length..];
                    oid = v == "(initial)" ? null : v;
                }
                else if (header.StartsWith("branch.head ", StringComparison.Ordinal))
                {
                    var v = header["branch.head ".Length..];
                    head = v == "(detached)" ? null : v;
                }
                else if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
                    upstream = header["branch.upstream ".Length..];
                else if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
                {
                    var parts = header["branch.ab ".Length..].Split(' ');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0].TrimStart('+'), out ahead);
                        int.TryParse(parts[1].TrimStart('-'), out behind);
                    }
                }
                continue;
            }

            switch (rec[0])
            {
                case '1':
                    // 1 XY sub mH mI mW hH hI <path>
                    files.Add(new ChangedFile(FieldTail(rec, 8), rec.Substring(2, 2), FileChangeKind.Modified));
                    break;
                case '2':
                    // 2 XY sub mH mI mW hH hI Xscore <path>\0<origPath>
                    var orig = i + 1 < records.Length ? records[++i] : null;
                    files.Add(new ChangedFile(FieldTail(rec, 9), rec.Substring(2, 2), FileChangeKind.Renamed, orig));
                    break;
                case 'u':
                    // u XY sub m1 m2 m3 mW h1 h2 h3 <path>
                    files.Add(new ChangedFile(FieldTail(rec, 10), rec.Substring(2, 2), FileChangeKind.Unmerged));
                    break;
                case '?':
                    files.Add(new ChangedFile(rec[2..], "??", FileChangeKind.Untracked));
                    break;
                case '!':
                    files.Add(new ChangedFile(rec[2..], "!!", FileChangeKind.Ignored));
                    break;
            }
        }

        return new WorktreeStatus(oid, head, upstream, ahead, behind, files);
    }

    /// <summary>Returns the text after the first <paramref name="spaces"/> space-separated fields (paths may contain spaces).</summary>
    private static string FieldTail(string rec, int spaces)
    {
        var idx = 0;
        for (var n = 0; n < spaces; n++)
        {
            idx = rec.IndexOf(' ', idx);
            if (idx < 0) return string.Empty;
            idx++;
        }
        return rec[idx..];
    }
}

public static class WorktreeListParser
{
    /// <summary>Parses <c>git worktree list --porcelain</c> (newline form; <c>-z</c> needs git 2.36).</summary>
    public static IReadOnlyList<WorktreeEntry> Parse(string stdout)
    {
        var result = new List<WorktreeEntry>();
        string? path = null, sha = null, branch = null, lockReason = null, prunableReason = null;
        bool bare = false, detached = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null)
                result.Add(new WorktreeEntry(path, sha, branch, bare, detached, locked, prunable, lockReason, prunableReason));
            path = sha = branch = lockReason = prunableReason = null;
            bare = detached = locked = prunable = false;
        }

        foreach (var raw in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\0');
            if (line.Length == 0) { Flush(); continue; }
            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? null : line[(space + 1)..];
            switch (key)
            {
                case "worktree": Flush(); path = value is null ? null : Unquote(value); break;
                case "HEAD": sha = value; break;
                case "branch": branch = value is not null && value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value; break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = true; lockReason = value; break;
                case "prunable": prunable = true; prunableReason = value; break;
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Undoes git's C-style quoting of a path (<c>core.quotePath</c>): a path with unusual characters, including any
    /// non-ASCII ones by default, comes back as <c>"caf\303\251 dir"</c>. Escapes become their characters and octal byte
    /// sequences are decoded as UTF-8. An unquoted path is returned as it is.
    /// </summary>
    public static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return text;
        var bytes = new List<byte>(text.Length);
        var end = text.Length - 1;
        for (var i = 1; i < end; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= end)
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var e = text[++i];
            if (e is >= '0' and <= '7' && i + 2 < end && text[i + 1] is >= '0' and <= '7' && text[i + 2] is >= '0' and <= '7')
            {
                bytes.Add((byte)Convert.ToInt32(text.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(e switch
            {
                'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13,
                _ => (byte)e, // \\ and \" (and anything unexpected) stand for themselves
            });
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
