using MattEland.HopeLab.Git;

namespace MattEland.HopeLab.Tests;

public class GitParserTests
{
    [Fact]
    public void PorcelainV2_parses_branch_headers_and_entries()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head feature/login",
            "# branch.upstream origin/feature/login",
            "# branch.ab +2 -3",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/file with spaces.cs",
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 new name.cs",
            "old name.cs",
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflicted.cs",
            "? untracked.txt",
            "! ignored.log",
            "");

        var s = PorcelainV2Parser.Parse(output);

        Assert.Equal("feature/login", s.Branch);
        Assert.Equal("origin/feature/login", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(3, s.Behind);
        Assert.Equal(5, s.Files.Count);
        Assert.Equal("src/file with spaces.cs", s.Files[0].Path);
        Assert.Equal("new name.cs", s.Files[1].Path);
        Assert.Equal("old name.cs", s.Files[1].OriginalPath);
        Assert.True(s.HasConflicts);
        Assert.Equal(3, s.TrackedChanges);
        Assert.Equal(1, s.UntrackedFiles);
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void PorcelainV2_handles_detached_and_initial()
    {
        var s = PorcelainV2Parser.Parse("# branch.oid (initial)\0# branch.head (detached)\0");
        Assert.Null(s.HeadSha);
        Assert.True(s.IsDetached);
        Assert.False(s.IsDirty);
        Assert.False(s.HasUpstream);
    }

    [Fact]
    public void Ignored_files_alone_are_not_dirty()
    {
        Assert.False(PorcelainV2Parser.Parse("# branch.head main\0! bin/\0").IsDirty);
    }

    [Fact]
    public void WorktreeList_parses_all_kinds()
    {
        var output = """
            worktree /src/repo
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree /src/repo.worktrees/feature-x
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feature/x
            locked reason here

            worktree /src/repo.worktrees/detached
            HEAD 3333333333333333333333333333333333333333
            detached
            prunable gitdir file points to non-existent location

            """;
        var list = WorktreeListParser.Parse(output);
        Assert.Equal(3, list.Count);
        Assert.Equal("main", list[0].Branch);
        Assert.Equal("feature/x", list[1].Branch);
        Assert.True(list[1].IsLocked);
        Assert.Equal("reason here", list[1].LockReason);
        Assert.True(list[2].IsDetached);
        Assert.True(list[2].IsPrunable);
        Assert.Null(list[2].Branch);
    }

    [Theory]
    [InlineData("git version 2.34.1", 2, 34, 1)]
    [InlineData("git version 2.45.1.windows.1", 2, 45, 1)]
    [InlineData("git version 2.50.0 (Apple Git-155)", 2, 50, 0)]
    public void GitVersion_parses(string text, int major, int minor, int patch)
    {
        var v = GitVersion.Parse(text);
        Assert.Equal(new GitVersion(major, minor, patch), v);
    }
}
