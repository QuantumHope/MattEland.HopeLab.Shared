namespace MattEland.HopeLab.Git;

public enum GitRefKind { LocalBranch, RemoteBranch, Tag, Head }

/// <summary>A ref decorating a commit, by its short name (<c>main</c>, <c>origin/main</c>, <c>v1.0</c>).</summary>
public sealed record GitRef(string Name, GitRefKind Kind);

/// <summary>One commit of <c>git log</c>, as parsed by <see cref="CommitLogParser"/>.</summary>
public sealed record GraphCommit(
    string Sha,
    IReadOnlyList<string> Parents,
    string Author,
    DateTimeOffset AuthorTime,
    string Subject,
    IReadOnlyList<GitRef> Refs);

/// <summary>
/// A line segment in one row of the graph. Upper segments run from the top of the row (lane <see cref="From"/>) to its
/// middle (lane <see cref="To"/>); lower ones from the middle to the bottom. A lane passing straight through a row has
/// one of each with <c>From == To</c>.
/// </summary>
public sealed record GraphEdge(int From, int To, bool Lower, int Color);

/// <summary>A commit placed in the graph: its lane and colour, and the segments drawn in its row.</summary>
public sealed record GraphRow(GraphCommit Commit, int Lane, int Color, IReadOnlyList<GraphEdge> Edges)
{
    /// <summary>Lanes this row touches: one more than the largest lane index used.</summary>
    public int Width => Math.Max(Lane, Edges.Count == 0 ? 0 : Edges.Max(e => Math.Max(e.From, e.To))) + 1;
}

/// <summary>Parses <see cref="CommitLogParser.Format"/> output from <c>git log --decorate=full</c>.</summary>
public static class CommitLogParser
{
    /// <summary>Fields split by 0x1F, records ended by 0x1E, so subjects and names may hold any other character.</summary>
    public const string Format = "%H%x1f%P%x1f%an%x1f%at%x1f%D%x1f%s%x1e";

    public static IReadOnlyList<GraphCommit> Parse(string output)
    {
        var commits = new List<GraphCommit>();
        foreach (var raw in output.Split('\x1e'))
        {
            var record = raw.TrimStart('\r', '\n');
            if (record.Length == 0) continue;
            var f = record.Split('\x1f');
            if (f.Length < 6 || f[0].Length == 0) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var time = long.TryParse(f[3], out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
            commits.Add(new GraphCommit(f[0], parents, f[2], time, f[5], ParseRefs(f[4])));
        }
        return commits;
    }

    /// <summary>
    /// Parses a <c>%D</c> decoration with full ref names: <c>HEAD -&gt; refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1</c>.
    /// A remote's symbolic HEAD (<c>refs/remotes/origin/HEAD</c>) is left out, and stash is never shown.
    /// </summary>
    public static IReadOnlyList<GitRef> ParseRefs(string decoration)
    {
        var refs = new List<GitRef>();
        foreach (var part in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part;
            if (name.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                refs.Add(new GitRef("HEAD", GitRefKind.Head));
                name = name["HEAD -> ".Length..];
            }
            if (name == "HEAD") { refs.Add(new GitRef("HEAD", GitRefKind.Head)); continue; }
            if (name.StartsWith("tag: ", StringComparison.Ordinal)) name = name["tag: ".Length..];

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
                refs.Add(new GitRef(name["refs/heads/".Length..], GitRefKind.LocalBranch));
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
                    refs.Add(new GitRef(name["refs/remotes/".Length..], GitRefKind.RemoteBranch));
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
                refs.Add(new GitRef(name["refs/tags/".Length..], GitRefKind.Tag));
        }
        return refs;
    }
}

/// <summary>
/// Assigns lanes to commits in topological order (children before parents), the way a git GUI draws history.
/// A commit takes the lane that was waiting for it (the leftmost, when several children wait, and the others converge
/// on it); its first parent continues that lane, and each further parent joins a lane already waiting for it or opens a new one. Freed lanes
/// are reused, and lanes never shift sideways, so every line is vertical except where a branch forks or merges.
/// </summary>
public static class GraphLayout
{
    public static IReadOnlyList<GraphRow> Compute(IReadOnlyList<GraphCommit> commits)
    {
        var rows = new List<GraphRow>(commits.Count);
        var lanes = new List<string?>();   // the sha each lane is waiting for; null is a free slot
        var colors = new List<int>();      // the colour of each lane's current line
        var nextColor = 0;

        foreach (var c in commits)
        {
            var edges = new List<GraphEdge>();
            var opened = new HashSet<int>(); // lanes whose line starts in this row's lower half

            // Lanes waiting for this commit converge on it; the leftmost becomes its lane.
            var waiting = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] == c.Sha) waiting.Add(i);

            int lane;
            int color;
            if (waiting.Count > 0)
            {
                lane = waiting[0];
                color = colors[lane];
            }
            else
            {
                // A branch tip nothing above points at: a new line in the first free slot.
                lane = FreeSlot(lanes, -1);
                color = nextColor++;
                Set(lanes, colors, lane, null, color);
            }

            // Upper half: lines arriving from the row above.
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null) continue;
                if (lanes[i] == c.Sha) edges.Add(new GraphEdge(i, lane, Lower: false, colors[i]));
                else edges.Add(new GraphEdge(i, i, Lower: false, colors[i]));
            }
            foreach (var i in waiting) lanes[i] = null;

            // Lower half: the first parent continues this lane, further parents join or open lanes.
            for (var p = 0; p < c.Parents.Count; p++)
            {
                var parent = c.Parents[p];
                var existing = lanes.IndexOf(parent);
                if (p == 0)
                {
                    // The first parent always continues this lane, even when another line already waits for it
                    // (a fork seen from above); the two converge on the parent's row, so lines never jog sideways.
                    Set(lanes, colors, lane, parent, color);
                    opened.Add(lane);
                    edges.Add(new GraphEdge(lane, lane, Lower: true, color));
                    continue;
                }
                if (existing >= 0)
                {
                    edges.Add(new GraphEdge(lane, existing, Lower: true, colors[existing]));
                    continue;
                }
                var slot = FreeSlot(lanes, lane);
                var newColor = nextColor++;
                Set(lanes, colors, slot, parent, newColor);
                opened.Add(slot);
                edges.Add(new GraphEdge(lane, slot, Lower: true, newColor));
            }

            // Lines passing by continue to the next row.
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is not null && !opened.Contains(i))
                    edges.Add(new GraphEdge(i, i, Lower: true, colors[i]));

            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }

            rows.Add(new GraphRow(c, lane, color, edges));
        }
        return rows;
    }

    private static int FreeSlot(List<string?> lanes, int except)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] is null && i != except) return i;
        return lanes.Count == except ? lanes.Count + 1 : lanes.Count;
    }

    private static void Set(List<string?> lanes, List<int> colors, int index, string? sha, int color)
    {
        while (lanes.Count <= index) { lanes.Add(null); colors.Add(0); }
        lanes[index] = sha;
        colors[index] = color;
    }
}
