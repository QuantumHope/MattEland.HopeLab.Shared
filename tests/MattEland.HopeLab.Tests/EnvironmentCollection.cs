namespace MattEland.HopeLab.Tests;

/// <summary>
/// Tests that set GH_TOKEN, GITHUB_TOKEN or an app's token variable. The process environment is shared, so they run
/// one at a time and never alongside anything else (xUnit otherwise runs test classes in parallel, and one test's
/// GH_TOKEN would leak into another's token lookup).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Process environment";
}
