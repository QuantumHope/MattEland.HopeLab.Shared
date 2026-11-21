using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MattEland.HopeLab.Themes.Tokens;

/// <summary>What a token holds, which decides how each generator writes it.</summary>
public enum TokenKind
{
    /// <summary>A plain colour: hex or rgb()/rgba().</summary>
    Color,
    /// <summary>A CSS <c>linear-gradient()</c>, or a colour.</summary>
    Gradient,
    /// <summary>A CSS box shadow, such as a glow.</summary>
    Shadow,
    /// <summary>A CSS font stack.</summary>
    Font,
    /// <summary>A unitless number, such as a weight, a line height or a stroke scale.</summary>
    Number,
    /// <summary>A length in pixels, written with <c>px</c> in CSS.</summary>
    Dimension,
    /// <summary>A bare word that names something, such as the chrome a theme draws.</summary>
    Keyword,
}

/// <summary>One token every theme shares: its neutral name, its CSS custom property, and what it is for.</summary>
/// <param name="Name">The neutral name, dotted (<c>accent.primary</c>). Generators derive their keys from it.</param>
/// <param name="Css">The CSS custom property without its dashes (<c>accent-pink</c>).</param>
/// <param name="Kind">What the value is, which decides how each generator writes it.</param>
/// <param name="Required">Whether every theme must give it a value. Otherwise <see cref="Default"/> applies, if any.</param>
/// <param name="Default">The value when a theme gives none. May reference other tokens as <c>{name}</c>.</param>
/// <param name="Description">What the token is for, for people choosing a value.</param>
/// <param name="Base">Whether the default belongs in the shared CSS block every theme starts from.</param>
public sealed record TokenDefinition(string Name, string Css, TokenKind Kind, bool Required, string? Default, string Description,
    bool Base = false)
{
    /// <summary>The Avalonia resource key: <c>Lab.</c> and the name in Pascal case (<c>Lab.Surface.HeaderEnd</c>).</summary>
    public string ResourceKey => "Lab." + string.Join('.', Name.Split('.').Select(TokenNames.Pascal));
}

/// <summary>The shared token list: every token a theme can set, in the order generators write them.</summary>
public sealed class TokenContract
{
    private readonly Dictionary<string, TokenDefinition> _byName;
    private readonly Dictionary<string, TokenDefinition> _byCss;

    public TokenContract(IReadOnlyList<TokenDefinition> tokens)
    {
        Tokens = tokens;
        _byName = tokens.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _byCss = tokens.ToDictionary(t => t.Css, StringComparer.Ordinal);
    }

    public IReadOnlyList<TokenDefinition> Tokens { get; }

    public TokenDefinition? this[string name] => _byName.GetValueOrDefault(name);

    public TokenDefinition? ByCss(string css) => _byCss.GetValueOrDefault(css);

    public static TokenContract Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("The contract is not a JSON object.");
        var list = new List<TokenDefinition>();
        foreach (var node in root["tokens"]?.AsArray() ?? throw new FormatException("The contract has no tokens."))
        {
            var o = node!.AsObject();
            var name = Str(o, "name") ?? throw new FormatException("A token has no name.");
            list.Add(new TokenDefinition(
                name,
                Str(o, "css") ?? throw new FormatException($"{name} has no css name."),
                Enum.Parse<TokenKind>(Str(o, "kind") ?? throw new FormatException($"{name} has no kind."), ignoreCase: true),
                o["required"]?.GetValue<bool>() ?? false,
                Str(o, "default"),
                Str(o, "description") ?? "",
                o["base"]?.GetValue<bool>() ?? false));
        }
        return new TokenContract(list);
    }

    internal static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.GetValue<string>() : null;
}

/// <summary>One theme's token file.</summary>
public sealed partial class ThemeSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary><c>dark</c> or <c>light</c>.</summary>
    public required string Scheme { get; init; }
    public bool HighContrast { get; init; }
    /// <summary>A Material Design icon name, without the <c>mdi-</c> prefix (<c>car-sports</c>).</summary>
    public string Icon { get; init; } = "palette-outline";
    /// <summary>A decorative line for the theme's chrome, or null.</summary>
    public string? Motto { get; init; }
    /// <summary>The prefix of the theme's own chrome properties in CSS (<c>kitt</c> for <c>--kitt-scanner</c>).</summary>
    public string ChromePrefix { get; init; } = "";
    /// <summary>Contract tokens this theme sets, in file order.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Tokens { get; init; }
    /// <summary>The theme's own decoration colours, which only its chrome uses, in file order.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Chrome { get; init; }

    public bool IsLight => Scheme == "light";

    public static ThemeSource Parse(string json)
    {
        var o = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("A theme file is not a JSON object.");
        static List<KeyValuePair<string, string>> Pairs(JsonNode? n) =>
            n is JsonObject obj ? obj.Select(p => KeyValuePair.Create(p.Key, p.Value!.GetValue<string>())).ToList() : [];
        var id = TokenContract.Str(o, "id") ?? throw new FormatException("A theme has no id.");
        return new ThemeSource
        {
            Id = id,
            Name = TokenContract.Str(o, "name") ?? id,
            Description = TokenContract.Str(o, "description") ?? "",
            Scheme = TokenContract.Str(o, "scheme") ?? throw new FormatException($"{id} has no scheme."),
            HighContrast = o["highContrast"]?.GetValue<bool>() ?? false,
            Icon = TokenContract.Str(o, "icon") ?? "palette-outline",
            Motto = TokenContract.Str(o, "motto"),
            ChromePrefix = TokenContract.Str(o, "chromePrefix") ?? id,
            Tokens = Pairs(o["tokens"]),
            Chrome = Pairs(o["chrome"]),
        };
    }

    /// <summary>A reference to another token inside a value: <c>{surface.panel}</c>, or <c>{chrome.amber}</c>.</summary>
    [GeneratedRegex(@"\{([a-z0-9][a-z0-9.-]*)\}")]
    internal static partial Regex Reference();
}

/// <summary>The contract and every theme, in catalogue order.</summary>
public sealed class TokenSet(TokenContract contract, IReadOnlyList<ThemeSource> themes)
{
    public TokenContract Contract { get; } = contract;
    public IReadOnlyList<ThemeSource> Themes { get; } = themes;

    /// <summary>
    /// Reads <c>contract.json</c>, <c>themes.json</c> (the catalogue order) and <c>themes/&lt;id&gt;.json</c> from a
    /// tokens folder.
    /// </summary>
    public static TokenSet Load(string folder)
    {
        var contract = TokenContract.Parse(File.ReadAllText(Path.Combine(folder, "contract.json")));
        var order = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "themes.json")))?["themes"]?.AsArray()
                    ?? throw new FormatException("themes.json has no themes list.");
        var themes = order.Select(n => n!.GetValue<string>())
            .Select(id => ThemeSource.Parse(File.ReadAllText(Path.Combine(folder, "themes", id + ".json"))))
            .ToList();
        return new TokenSet(contract, themes);
    }

    public ResolvedTheme Resolve(ThemeSource theme) => new(Contract, theme);
}

/// <summary>A theme's values with defaults applied and references followed.</summary>
public sealed class ResolvedTheme
{
    private readonly TokenContract _contract;
    private readonly Dictionary<string, string> _tokens;
    private readonly Dictionary<string, string> _chrome;

    internal ResolvedTheme(TokenContract contract, ThemeSource theme)
    {
        _contract = contract;
        Theme = theme;
        _tokens = theme.Tokens.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _chrome = theme.Chrome.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public ThemeSource Theme { get; }

    /// <summary>The value as written: the theme's own, else the contract's default, else null. References stay.</summary>
    public string? Raw(string name)
    {
        if (name.StartsWith("chrome.", StringComparison.Ordinal)) return _chrome.GetValueOrDefault(name[7..]);
        return _tokens.TryGetValue(name, out var v) ? v : _contract[name]?.Default;
    }

    /// <summary>The value with every reference replaced by what it refers to, or null when there is none.</summary>
    public string? Value(string name)
    {
        var v = Raw(name);
        for (var i = 0; v is not null && i < 10 && v.Contains('{'); i++)
            v = ThemeSource.Reference().Replace(v, m => Raw(m.Groups[1].Value) ?? m.Value);
        return v?.Trim();
    }

    public TokenColor? Color(string name) => TokenColor.Parse(Value(name));
}

public static class TokenNames
{
    /// <summary><c>header-end</c> → <c>HeaderEnd</c>; <c>2xl</c> stays <c>2xl</c>.</summary>
    public static string Pascal(string part) =>
        string.Concat(part.Split('-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
