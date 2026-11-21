using MattEland.HopeLab.Themes.Tokens;

namespace MattEland.HopeLab.Themes.Tests;

/// <summary>
/// The rules every theme is held to, wherever it's drawn: every required token set, and text and accents that pass
/// contrast. The floors are WCAG 1.4.3 AA (4.5:1) for text and for the accents, which are used as text and icon colour,
/// and 1.4.6 AAA (7:1) in a high-contrast theme.
/// </summary>
public sealed class TokenRuleTests
{
    private static readonly string[] Surfaces = ["surface.canvas", "surface.panel", "surface.elevated", "surface.header"];
    private static readonly string[] Text = ["text.primary", "text.secondary", "text.mono"];
    private static readonly string[] Accents =
        ["accent.primary", "accent.highlight", "accent.link", "accent.info", "accent.success", "accent.error", "accent.warning"];

    /// <summary>The accents' meanings, which must stay apart in every theme.</summary>
    private const double MinAccentDeltaE = 12;

    public static IEnumerable<object[]> Themes => Repo.ThemeIds;

    [Fact]
    public void The_catalogue_lists_every_theme_file_once_and_names_a_default()
    {
        var files = Directory.GetFiles(Path.Combine(Repo.Tokens, "themes"), "*.json").Select(Path.GetFileNameWithoutExtension).Order();
        Assert.Equal(files, Repo.Set.Themes.Select(t => t.Id).Order());
        Assert.Contains(ThemeGenerator.DefaultTheme(Repo.Tokens), Repo.Set.Themes.Select(t => t.Id));
    }

    [Fact]
    public void The_contract_names_each_token_and_property_once()
    {
        var c = Repo.Set.Contract.Tokens;
        Assert.Equal(c.Count, c.Select(t => t.Name).Distinct().Count());
        Assert.Equal(c.Count, c.Select(t => t.Css).Distinct().Count());
        Assert.All(c.Where(t => !t.Required), t => Assert.NotNull(t.Default));
    }

    [Theory, MemberData(nameof(Themes))]
    public void Sets_every_required_token_and_nothing_outside_the_contract(string id)
    {
        var t = Repo.Theme(id).Theme;
        var set = t.Tokens.Select(p => p.Key).ToHashSet();
        Assert.Empty(Repo.Set.Contract.Tokens.Where(d => d.Required && !set.Contains(d.Name)).Select(d => d.Name));
        Assert.All(set, n => Assert.NotNull(Repo.Set.Contract[n]));
        Assert.Contains(t.Scheme, new[] { "dark", "light" });
    }

    [Theory, MemberData(nameof(Themes))]
    public void Every_reference_points_at_a_token_the_theme_has(string id)
    {
        var theme = Repo.Theme(id);
        foreach (var name in Repo.Set.Contract.Tokens.Select(d => d.Name).Concat(theme.Theme.Chrome.Select(p => "chrome." + p.Key)))
            Assert.DoesNotContain("{", theme.Value(name) ?? "");
    }

    [Theory, MemberData(nameof(Themes))]
    public void Gives_text_accent_and_ink_tokens_plain_hex_values(string id)
    {
        var theme = Repo.Theme(id);
        var notHex = Surfaces.Concat(Text).Concat(Accents).Append("ink.icon").Append("ink.overlay")
            .Where(n => !System.Text.RegularExpressions.Regex.IsMatch(theme.Value(n) ?? "", "^#[0-9a-fA-F]{6}$"));
        Assert.Empty(notHex);
    }

    [Theory, MemberData(nameof(Themes))]
    public void Has_text_that_passes_contrast_on_every_surface(string id)
    {
        var theme = Repo.Theme(id);
        Assert.Empty(Failures(theme, Text, theme.Theme.HighContrast ? 7 : 4.5));
    }

    [Theory, MemberData(nameof(Themes))]
    public void Has_accents_that_pass_contrast_on_every_surface(string id)
    {
        var theme = Repo.Theme(id);
        Assert.Empty(Failures(theme, Accents, theme.Theme.HighContrast ? 7 : 4.5));
    }

    [Theory, MemberData(nameof(Themes))]
    public void Has_icon_ink_that_reads_on_every_accent(string id)
    {
        var theme = Repo.Theme(id);
        var ink = theme.Color("ink.icon")!.Value;
        Assert.Empty(Accents.Select(a => (a, r: ink.Contrast(theme.Color(a)!.Value))).Where(x => x.r < 4.5)
            .Select(x => $"ink.icon on {x.a}: {x.r:0.00}"));
    }

    [Theory, MemberData(nameof(Themes))]
    public void Keeps_the_accents_apart_because_each_one_means_something(string id)
    {
        var theme = Repo.Theme(id);
        var close = new List<string>();
        for (var i = 0; i < Accents.Length; i++)
        for (var j = i + 1; j < Accents.Length; j++)
        {
            var d = theme.Color(Accents[i])!.Value.DeltaE(theme.Color(Accents[j])!.Value);
            if (d < MinAccentDeltaE) close.Add($"{Accents[i]} / {Accents[j]}: ΔE {d:0.0}");
        }
        Assert.Empty(close);
    }

    private static IEnumerable<string> Failures(ResolvedTheme theme, string[] inks, double floor) =>
        from s in Surfaces
        from i in inks
        let r = theme.Color(s)!.Value.Contrast(theme.Color(i)!.Value)
        where r < floor
        select $"{i} on {s}: {r:0.00}";
}
