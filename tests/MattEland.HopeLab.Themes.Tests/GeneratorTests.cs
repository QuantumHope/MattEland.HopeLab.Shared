using MattEland.HopeLab.Themes.Tokens;

namespace MattEland.HopeLab.Themes.Tests;

public sealed class GeneratorTests
{
    private static readonly GeneratedFiles Output = ThemeGenerator.Generate(Repo.Set, ThemeGenerator.DefaultTheme(Repo.Tokens));

    [Fact]
    public void The_generated_files_in_the_repository_are_up_to_date()
    {
        var stale = Output.Files.Where(f =>
        {
            var path = Path.Combine(Repo.Root, f.Key);
            return !File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings() != f.Value.ReplaceLineEndings();
        }).Select(f => f.Key);
        Assert.Empty(stale); // run: dotnet run --project tools/ThemeTokens -- generate
    }

    [Fact]
    public void There_are_no_generated_files_for_themes_that_no_longer_exist()
    {
        var css = Directory.GetFiles(Path.Combine(Repo.Root, ThemeGenerator.CssFolder), "*.css");
        var axaml = Directory.GetFiles(Path.Combine(Repo.Root, ThemeGenerator.AvaloniaProject, "Themes"), "*.axaml");
        var expected = Output.Files.Keys.Select(k => Path.GetFullPath(Path.Combine(Repo.Root, k))).ToHashSet();
        Assert.All(css.Concat(axaml), f => Assert.Contains(Path.GetFullPath(f), expected));
    }

    [Fact]
    public void Each_theme_CSS_block_holds_exactly_the_theme_tokens_and_its_scheme()
    {
        foreach (var theme in Repo.Set.Themes)
        {
            var block = Assert.Single(CssReader.Parse(Output.Files[$"{ThemeGenerator.CssFolder}/{theme.Id}.css"]));
            Assert.Equal([theme.Id], block.ThemeIds);
            var decls = block.Declarations.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(theme.Scheme, decls["color-scheme"]);
            foreach (var (name, _) in theme.Tokens) Assert.True(decls.ContainsKey(Repo.Set.Contract[name]!.Css), $"{theme.Id}: {name}");
            foreach (var (name, _) in theme.Chrome) Assert.True(decls.ContainsKey($"{theme.ChromePrefix}-{name}"), $"{theme.Id}: chrome {name}");
            Assert.Equal(1 + theme.Tokens.Count + theme.Chrome.Count, decls.Count);
        }
    }

    [Fact]
    public void Only_the_default_theme_is_written_onto_root()
    {
        var roots = Repo.Set.Themes.Where(t => Output.Files[$"{ThemeGenerator.CssFolder}/{t.Id}.css"].Contains(":root,")).Select(t => t.Id);
        Assert.Equal([ThemeGenerator.DefaultTheme(Repo.Tokens)], roots);
    }

    [Fact]
    public void The_index_imports_the_default_theme_before_any_other()
    {
        // The default block is also on :root, which is as specific as [data-theme="id"], so the later import wins on the
        // root element: imported after Light, Dark's values would replace Light's whichever theme was chosen.
        var imports = System.Text.RegularExpressions.Regex.Matches(Output.Files[$"{ThemeGenerator.CssFolder}/index.css"], @"@import ""\./([\w-]+)\.css"";")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal("base", imports[0]);
        Assert.Equal(ThemeGenerator.DefaultTheme(Repo.Tokens), imports[1]);
        Assert.Equal(Repo.Set.Themes.Select(t => t.Id).Order(), imports.Skip(1).Order());
    }

    [Fact]
    public void References_become_var_in_CSS()
    {
        var contract = new TokenContract([
            new TokenDefinition("surface.panel", "surface-panel", TokenKind.Color, true, null, ""),
            new TokenDefinition("border.focus", "border-focus", TokenKind.Color, true, null, ""),
        ]);
        var theme = new ThemeSource
        {
            Id = "test", Name = "Test", Scheme = "dark", ChromePrefix = "tst",
            Tokens = [KeyValuePair.Create("surface.panel", "#101010"), KeyValuePair.Create("border.focus", "{chrome.ring}")],
            Chrome = [KeyValuePair.Create("ring", "{surface.panel}")],
        };
        var decls = CssReader.Parse(new CssWriter(contract).WriteTheme(theme, isDefault: false))[0].Declarations.ToDictionary();
        Assert.Equal("var(--tst-ring)", decls["border-focus"]);
        Assert.Equal("var(--surface-panel)", decls["tst-ring"]);
    }

    [Fact]
    public void A_token_outside_the_contract_is_an_error_not_a_silent_drop()
    {
        var contract = new TokenContract([]);
        var theme = new ThemeSource
        {
            Id = "test", Name = "Test", Scheme = "dark", Tokens = [KeyValuePair.Create("surface.panle", "#101010")], Chrome = [],
        };
        Assert.Throws<InvalidOperationException>(() => new CssWriter(contract).WriteTheme(theme, isDefault: false));
    }

    [Fact]
    public void The_catalogue_lists_every_theme_in_order() =>
        Assert.Equal(Repo.Set.Themes.Select(t => t.Id), ThemeCatalog.All.Select(t => t.Id));

    [Fact]
    public void Every_face_the_themes_name_as_bundled_has_a_font_file()
    {
        var files = Directory.GetFiles(Path.Combine(Repo.Root, ThemeGenerator.AvaloniaProject, "Assets", "Fonts"), "*.ttf")
            .Select(f => Path.GetFileNameWithoutExtension(f).Replace(" ", "").ToLowerInvariant()).ToList();
        var missing = AxamlWriter.BundledFaces.Where(face =>
        {
            var key = face.Replace(" ", "").ToLowerInvariant();
            return !files.Any(f => f.StartsWith(key, StringComparison.Ordinal));
        });
        Assert.Empty(missing);
    }
}
