using MattEland.HopeLab.Git;

namespace MattEland.HopeLab.Tests;

public class CommitGraphTests
{
    private static GraphCommit C(string sha, params string[] parents) => new(sha, parents, "a", DateTimeOffset.UnixEpoch, sha, []);

    [Fact]
    public void Parses_records_with_any_characters_in_the_subject()
    {
        // \u001f, not \x1f: C# reads "\x1fbbb" as one character.
        var output = "aaa\u001fbbb ccc\u001fAda Lovelace\u001f1700000000\u001fHEAD -> refs/heads/main, refs/remotes/origin/main, refs/remotes/origin/HEAD, tag: refs/tags/v1\u001fFix: a, b | c\u001e\n"
                     + "bbb\u001f\u001fAda\u001f1600000000\u001f\u001froot\u001e\n";

        var commits = CommitLogParser.Parse(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal(["bbb", "ccc"], commits[0].Parents);
        Assert.Equal("Fix: a, b | c", commits[0].Subject);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), commits[0].AuthorTime);
        Assert.Equal(
            [new GitRef("HEAD", GitRefKind.Head), new GitRef("main", GitRefKind.LocalBranch), new GitRef("origin/main", GitRefKind.RemoteBranch), new GitRef("v1", GitRefKind.Tag)],
            commits[0].Refs);
        Assert.Empty(commits[1].Parents);
        Assert.Empty(commits[1].Refs);
    }

    [Fact]
    public void A_straight_line_stays_in_lane_zero()
    {
        var rows = GraphLayout.Compute([C("c", "b"), C("b", "a"), C("a")]);

        Assert.All(rows, r => Assert.Equal(0, r.Lane));
        Assert.All(rows, r => Assert.Equal(1, r.Width));
        Assert.DoesNotContain(rows[0].Edges, e => !e.Lower);          // a tip: nothing arrives from above
        Assert.Contains(rows[1].Edges, e => e is { Lower: false, From: 0, To: 0 });
        Assert.DoesNotContain(rows[2].Edges, e => e.Lower);           // a root: nothing leaves below
    }

    [Fact]
    public void A_merge_opens_a_lane_for_its_second_parent_which_rejoins_at_the_fork()
    {
        // m merges f into main; f and b both come from a.
        var rows = GraphLayout.Compute([C("m", "b", "f"), C("f", "a"), C("b", "a"), C("a")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Contains(rows[0].Edges, e => e is { Lower: true, From: 0, To: 1 });   // to the second parent
        Assert.Equal(1, rows[1].Lane);                                               // f sits in the new lane
        Assert.Contains(rows[1].Edges, e => e is { Lower: true, From: 0, To: 0 });   // main passes by f
        Assert.Equal(0, rows[2].Lane);
        Assert.Contains(rows[2].Edges, e => e is { Lower: true, From: 1, To: 1 });   // f's line passes by b
        Assert.Equal(0, rows[3].Lane);
        Assert.Contains(rows[3].Edges, e => e is { Lower: false, From: 1, To: 0 });  // and converges on a
        Assert.Equal(2, rows[1].Width);
        Assert.NotEqual(rows[0].Color, rows[1].Color);
    }

    [Fact]
    public void Two_branch_tips_get_separate_lanes_and_a_freed_lane_is_reused()
    {
        // x and y are both tips off a; z is a later, unrelated tip off w.
        var rows = GraphLayout.Compute([C("x", "a"), C("y", "a"), C("a"), C("z", "w"), C("w")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Equal(1, rows[1].Lane);
        Assert.Contains(rows[1].Edges, e => e is { Lower: true, From: 1, To: 1 });   // y keeps its own lane down to a
        Assert.Equal(0, rows[2].Lane);
        Assert.Contains(rows[2].Edges, e => e is { Lower: false, From: 1, To: 0 });  // where the two lines converge
        Assert.Equal(0, rows[3].Lane);                                               // lanes free again after the root
    }

    [Fact]
    public void A_line_waiting_for_an_older_commit_passes_straight_through_a_merge_that_joins_it()
    {
        // m1 and m2 both merge t; t's lane must keep running through m2's row down to t.
        var rows = GraphLayout.Compute([C("m1", "m2", "t"), C("m2", "a", "t"), C("t", "a"), C("a")]);

        var m2 = rows[1];
        Assert.Contains(m2.Edges, e => e is { Lower: true, From: 0, To: 1 });   // m2 joins t's lane
        Assert.Contains(m2.Edges, e => e is { Lower: true, From: 1, To: 1 });   // and t's lane continues
        Assert.Contains(m2.Edges, e => e is { Lower: false, From: 1, To: 1 });
    }
}
