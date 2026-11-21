using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using MattEland.HopeLab.Themes.Tokens;

namespace MattEland.HopeLab.Themes.Tests;

/// <summary>Loads each generated theme into Avalonia, and renders all of them side by side to a PNG.</summary>
public sealed class AvaloniaThemeTests
{
    public static IEnumerable<object[]> Themes => Repo.ThemeIds;

    private static IResourceDictionary Load(string id) => ThemeCatalog.Find(id)!.CreateResources();

    private static object Get(IResourceDictionary d, string key) =>
        d.TryGetResource(key, null, out var v) && v is not null ? v : throw new KeyNotFoundException(key);

    [Theory, MemberData(nameof(Themes))]
    public Task Loads_with_every_shared_key(string id) => Ui.Run(() =>
    {
        var d = Load(id);
        var theme = Repo.Theme(id);
        foreach (var t in Repo.Set.Contract.Tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.Color when TokenColor.Parse(theme.Value(t.Name)) is { } c:
                    var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Get(d, t.ResourceKey));
                    var color = Assert.IsType<Color>(Get(d, t.ResourceKey + ".Color"));
                    Assert.Equal(brush.Color, color);
                    Assert.Equal(Color.Parse(c.ToString()), color); // chart series too: every token reaches 3:1, so none is lifted
                    break;
                case TokenKind.Gradient:
                    Assert.IsAssignableFrom<IBrush>(Get(d, t.ResourceKey));
                    break;
                case TokenKind.Font:
                    Assert.IsType<FontFamily>(Get(d, t.ResourceKey));
                    break;
            }
        }
        foreach (var key in new[] { "Lab.Accent.Primary.Hot", "Lab.Wash.Hover", "Lab.Scrim", "Lab.Viz.Series7", "Lab.Viz.Other" })
            Assert.IsAssignableFrom<ISolidColorBrush>(Get(d, key));
        Assert.IsType<double>(Get(d, "Lab.Type.Body.Size"));
        Assert.IsType<FontWeight>(Get(d, "Lab.Type.Heading.Weight"));
        Assert.IsType<CornerRadius>(Get(d, "Lab.Radius"));
        Assert.IsType<BoxShadows>(Get(d, "Lab.Glow.Primary"));
        Assert.Equal(id, Get(d, "Lab.Theme.Id"));
        foreach (var (k, _) in theme.Theme.Chrome.Where(c => TokenColor.Parse(theme.Value("chrome." + c.Key)) is not null))
            Assert.IsAssignableFrom<IBrush>(Get(d, "Lab.Chrome." + TokenNames.Pascal(k)));
    });

    [Fact]
    public Task Each_theme_can_also_be_included_by_its_address() => Ui.Run(() =>
    {
        foreach (var info in ThemeCatalog.All)
        {
            var include = new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://MattEland.HopeLab.Tests/")) { Source = info.Source };
            Assert.Equal(info.Id, Get(include.Loaded, "Lab.Theme.Id"));
        }
    });

    [Fact]
    public Task Every_bundled_face_loads_from_the_package() => Ui.Run(() =>
    {
        var missing = AxamlWriter.BundledFaces.Select(face =>
        {
            var family = new FontFamily($"avares://MattEland.HopeLab.Themes/Assets/Fonts#{face}");
            var ok = FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var g);
            return (face, got: ok ? g!.FamilyName : "(none)");
        })
        // A variable face reports its default instance's name ("Oxanium ExtraLight"), which still means it was found
        .Where(x => !x.got.StartsWith(x.face, StringComparison.OrdinalIgnoreCase)).Select(x => $"{x.face} → {x.got}");
        Assert.Empty(missing);
    });

    [Fact]
    public Task Every_theme_renders_side_by_side() => Ui.Run(async () =>
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(12) };
        foreach (var info in ThemeCatalog.All) grid.Children.Add(Card(info, Load(info.Id)));
        var window = new Window { Width = 1640, Height = 1500, Content = grid, Background = Brushes.Black };
        window.Show();
        for (var i = 0; i < 6; i++)
        {
            Ui.Pump();
            await Task.Delay(10);
        }
        var path = ShotPath("themes.png");
        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            frame.Save(path);
        }
        window.Close();
        Assert.True(new FileInfo(path).Length > 50_000);
    });

    /// <summary>One theme as a card: its header, text on its panel, its seven accents, and its own chrome colours.</summary>
    private static Control Card(ThemeInfo info, IResourceDictionary d)
    {
        T R<T>(string key) => (T)Get(d, key);
        TextBlock Text(string text, string brush, string font, string size) => new()
        {
            Text = text, Foreground = R<IBrush>(brush), FontFamily = R<FontFamily>(font), FontSize = R<double>(size),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Border Swatch(IBrush b, double w = 26) => new()
        {
            Background = b, Width = w, Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0),
            BorderBrush = R<IBrush>("Lab.Border.Subtle"), BorderThickness = new Thickness(1),
        };

        var accents = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var a in new[] { "Primary", "Highlight", "Link", "Info", "Success", "Error", "Warning" })
            accents.Children.Add(Swatch(R<IBrush>($"Lab.Accent.{a}")));

        var chrome = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        chrome.Children.Add(Swatch(R<IBrush>("Lab.Persona.Face"), 14));
        chrome.Children.Add(Swatch(R<IBrush>("Lab.Persona.Signature"), 14));
        foreach (var key in d.Keys.OfType<string>().Where(k => k.StartsWith("Lab.Chrome.", StringComparison.Ordinal)).Order().Take(12))
            if (Get(d, key) is IBrush b) chrome.Children.Add(Swatch(b, 14));

        var panel = new Border
        {
            Background = R<IBrush>("Lab.Surface.Panel"), CornerRadius = R<CornerRadius>("Lab.Radius"), Padding = new Thickness(10),
            Margin = new Thickness(10), BorderBrush = R<IBrush>("Lab.Border.Subtle"), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    Text("Body text on the panel", "Lab.Text.Primary", "Lab.Font.Body", "Lab.Type.Body.Size"),
                    Text("Secondary text and captions", "Lab.Text.Secondary", "Lab.Font.Body", "Lab.Type.Label.Size"),
                    Text("MONO 0x2A · 12:04:51", "Lab.Text.Mono", "Lab.Font.Mono", "Lab.Type.Machine.Size"),
                    accents,
                    chrome,
                },
            },
        };
        var header = new Border
        {
            Background = R<IBrush>("Lab.Gradient.Header"), Padding = new Thickness(10, 6),
            BorderBrush = R<IBrush>("Lab.Border.Header"), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Children =
                {
                    Text(info.Name, "Lab.Text.Primary", "Lab.Font.Display", "Lab.Type.Subheading.Size"),
                    Text($"{info.Id} · {(info.IsLight ? "light" : "dark")}{(info.HighContrast ? " · high contrast" : "")}",
                        "Lab.Text.Secondary", "Lab.Font.Mono", "Lab.Type.Micro.Size"),
                },
            },
        };
        return new Border
        {
            Background = R<IBrush>("Lab.Surface.Canvas"), Margin = new Thickness(6), ClipToBounds = true,
            CornerRadius = new CornerRadius(8), Height = 226,
            Child = new DockPanel { Children = { header, panel } },
        }.Also(b => DockPanel.SetDock(header, Dock.Top));
    }

    private static string ShotPath(string name)
    {
        var folder = Environment.GetEnvironmentVariable("THEME_SCREENSHOTS") is { Length: > 0 } env
            ? env
            : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }
}

internal static class Fluent
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
