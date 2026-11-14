using MattEland.HopeLab.Git;
using Xunit;

namespace MattEland.HopeLab.Tests;

public class BranchRefsTests
{
    [Fact]
    public void Local_branches_keep_their_name_and_remote_ones_get_their_remote()
    {
        var (local, remote) = GitClient.ParseBranchRefs(
            "refs/heads/main\nrefs/heads/feat/x\nrefs/remotes/origin/HEAD\nrefs/remotes/origin/main\nrefs/remotes/upstream/dev\n");
        Assert.Equal(["feat/x", "main"], local);
        Assert.Equal(["origin/main", "upstream/dev"], remote);
    }
}
