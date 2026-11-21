using MattEland.HopeLab.Themes.Tokens;

namespace MattEland.HopeLab.Themes.Tests;

/// <summary>The repository's token set, read once.</summary>
public static class Repo
{
    public static string Root { get; } = ThemeGenerator.FindRepoRoot(AppContext.BaseDirectory)
                                         ?? throw new InvalidOperationException("Tests run from inside the repository.");

    public static string Tokens => Path.Combine(Root, "tokens");

    private static readonly Lazy<TokenSet> LazySet = new(() => TokenSet.Load(Tokens));

    public static TokenSet Set => LazySet.Value;

    public static IEnumerable<object[]> ThemeIds => Set.Themes.Select(t => new object[] { t.Id });

    public static ResolvedTheme Theme(string id) => Set.Resolve(Set.Themes.Single(t => t.Id == id));
}
