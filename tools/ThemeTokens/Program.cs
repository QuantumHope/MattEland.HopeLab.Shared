// Theme token tool. Run from anywhere inside the repository:
//
//   dotnet run --project tools/ThemeTokens -- generate            write tokens/css/ and the Avalonia themes from tokens/
//   dotnet run --project tools/ThemeTokens -- generate --check    fail if any generated file is out of date
//   dotnet run --project tools/ThemeTokens -- import  <web layout options>    seed tokens/themes/ from a web client's CSS
//   dotnet run --project tools/ThemeTokens -- compare <web layout options>    check generated CSS against that CSS
//
// Web layout options (paths to a checkout of the web client that the themes came from):
//   --registry <themes.ts>      the theme registry: id, label, description, scheme, icon, highContrast, motto
//   --colors <colors.css>       the default theme's block (also on :root) and the light block
//   --themes <folder>           one <id>.css per other theme
//   --typography <file.css>     (compare) the base font and type-scale block
//   --spacing <file.css>        (compare) the base spacing block

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MattEland.HopeLab.Themes.Tokens;

var root = ThemeGenerator.FindRepoRoot(Directory.GetCurrentDirectory())
           ?? ThemeGenerator.FindRepoRoot(AppContext.BaseDirectory)
           ?? throw new InvalidOperationException("Run from inside the MattEland.HopeLab.Shared repository.");
var tokens = Path.Combine(root, "tokens");
var command = args.FirstOrDefault() ?? "generate";
var options = Options(args.Skip(1).ToArray());

switch (command)
{
    case "generate":
        return Generate(options.ContainsKey("check"));
    case "import":
        return Import();
    case "compare":
        return Compare();
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use generate, import or compare.");
        return 2;
}

int Generate(bool check)
{
    var set = TokenSet.Load(tokens);
    var result = ThemeGenerator.Generate(set, ThemeGenerator.DefaultTheme(tokens));
    var stale = new List<string>();
    foreach (var (path, content) in result.Files)
    {
        var full = Path.Combine(root, path);
        var same = File.Exists(full) && File.ReadAllText(full) == content;
        if (same) continue;
        stale.Add(path);
        if (check) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
    // Themes deleted from the token set leave generated files behind; remove them
    Directory.CreateDirectory(Path.Combine(root, ThemeGenerator.CssFolder));
    Directory.CreateDirectory(Path.Combine(root, ThemeGenerator.AvaloniaProject, "Themes"));
    var expected = result.Files.Keys.Select(p => Path.GetFullPath(Path.Combine(root, p))).ToHashSet();
    var orphans = Directory.GetFiles(Path.Combine(root, ThemeGenerator.CssFolder), "*.css")
        .Concat(Directory.GetFiles(Path.Combine(root, ThemeGenerator.AvaloniaProject, "Themes"), "*.axaml"))
        .Where(f => !expected.Contains(Path.GetFullPath(f))).ToList();
    foreach (var o in orphans)
    {
        stale.Add(Path.GetRelativePath(root, o));
        if (!check) File.Delete(o);
    }

    foreach (var w in result.Warnings) Console.WriteLine($"  note: {w}");
    if (check)
    {
        foreach (var s in stale) Console.Error.WriteLine($"  out of date: {s}");
        Console.WriteLine(stale.Count == 0 ? "Generated files are up to date." : "Run: dotnet run --project tools/ThemeTokens -- generate");
        return stale.Count == 0 ? 0 : 1;
    }
    Console.WriteLine($"{set.Themes.Count} themes: wrote {stale.Count} file(s), {result.Files.Count - (stale.Count - orphans.Count)} unchanged.");
    return 0;
}

int Import()
{
    var contract = TokenContract.Parse(File.ReadAllText(Path.Combine(tokens, "contract.json")));
    var registry = Registry.Parse(File.ReadAllText(Need("registry")));
    var blocks = WebBlocks();
    var defaultId = blocks.First(b => b.Selector.Contains(":root", StringComparison.Ordinal)).ThemeIds.First();

    Directory.CreateDirectory(Path.Combine(tokens, "themes"));
    var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    foreach (var entry in registry)
    {
        var decls = blocks.Where(b => b.ThemeIds.Contains(entry.Id)).SelectMany(b => b.Declarations).ToList();
        if (decls.Count == 0) throw new InvalidOperationException($"{entry.Id}: no CSS block names it.");
        var scheme = decls.LastOrDefault(d => d.Key == "color-scheme").Value;
        if (scheme is not null && scheme != entry.Scheme)
            throw new InvalidOperationException($"{entry.Id}: the registry says {entry.Scheme}, the CSS says {scheme}.");

        // The theme's own properties share a prefix: its id, or a short form of it (mech- for mechanicus)
        var own = decls.Select(d => d.Key).Where(k => k != "color-scheme" && contract.ByCss(k) is null).ToList();
        var prefix = own.Count == 0 ? entry.Id : own.GroupBy(k => k[..k.IndexOf('-')]).MaxBy(g => g.Count())!.Key;

        string Ref(string value) => Regex.Replace(value, @"var\(--([a-z0-9-]+)\)", m =>
        {
            var css = m.Groups[1].Value;
            if (contract.ByCss(css) is { } t) return "{" + t.Name + "}";
            if (css.StartsWith(prefix + "-", StringComparison.Ordinal)) return "{chrome." + css[(prefix.Length + 1)..] + "}";
            throw new InvalidOperationException($"{entry.Id}: var(--{css}) is neither a shared token nor one of the theme's own.");
        });

        var tokenValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var chrome = new JsonObject();
        foreach (var (key, value) in decls)
        {
            if (key == "color-scheme") continue;
            if (contract.ByCss(key) is { } t)
            {
                var v = t.Kind == TokenKind.Dimension && value.EndsWith("px", StringComparison.Ordinal) ? value[..^2] : value;
                tokenValues[t.Name] = Ref(v);
            }
            else if (key.StartsWith(prefix + "-", StringComparison.Ordinal)) chrome[key[(prefix.Length + 1)..]] = Ref(value);
            else throw new InvalidOperationException($"{entry.Id}: --{key} is neither a shared token nor prefixed {prefix}-.");
        }

        var tokenObject = new JsonObject();
        foreach (var t in contract.Tokens)
            if (tokenValues.TryGetValue(t.Name, out var v)) tokenObject[t.Name] = v;

        var doc = new JsonObject
        {
            ["id"] = entry.Id,
            ["name"] = entry.Label,
            ["description"] = entry.Description,
            ["scheme"] = entry.Scheme,
            ["highContrast"] = entry.HighContrast,
            ["icon"] = entry.Icon.StartsWith("mdi-", StringComparison.Ordinal) ? entry.Icon[4..] : entry.Icon,
        };
        if (entry.Motto is not null) doc["motto"] = entry.Motto;
        if (prefix != entry.Id) doc["chromePrefix"] = prefix;
        doc["tokens"] = tokenObject;
        if (chrome.Count > 0) doc["chrome"] = chrome;
        File.WriteAllText(Path.Combine(tokens, "themes", entry.Id + ".json"), doc.ToJsonString(json) + "\n", new UTF8Encoding(false));
    }

    var index = new JsonObject
    {
        ["description"] = "The shared themes in catalogue order, and the one apps start on.",
        ["default"] = defaultId,
        ["themes"] = new JsonArray(registry.Select(r => (JsonNode)JsonValue.Create(r.Id)!).ToArray()),
    };
    File.WriteAllText(Path.Combine(tokens, "themes.json"), index.ToJsonString(json) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"Imported {registry.Count} themes; default {defaultId}.");
    return 0;
}

int Compare()
{
    var set = TokenSet.Load(tokens);
    var generated = ThemeGenerator.Generate(set, ThemeGenerator.DefaultTheme(tokens)).Files;
    var web = WebBlocks();
    var differences = 0;

    void Diff(string what, IEnumerable<KeyValuePair<string, string>> expected, IEnumerable<KeyValuePair<string, string>> actual)
    {
        var e = expected.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        var a = actual.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        foreach (var k in e.Keys.Union(a.Keys).Order())
        {
            e.TryGetValue(k, out var ev);
            a.TryGetValue(k, out var av);
            if (ev == av) continue;
            differences++;
            Console.WriteLine($"  {what}: --{k}: web '{ev ?? "(none)"}', generated '{av ?? "(none)"}'");
        }
    }

    foreach (var t in set.Themes)
    {
        var expected = web.Where(b => b.ThemeIds.Contains(t.Id)).SelectMany(b => b.Declarations);
        var actual = CssReader.Parse(generated[$"{ThemeGenerator.CssFolder}/{t.Id}.css"]).SelectMany(b => b.Declarations);
        Diff(t.Id, expected, actual);
    }

    var baseNames = set.Contract.Tokens.Where(t => t.Base).Select(t => t.Css).ToHashSet();
    var baseExpected = new[] { Need("typography"), Need("spacing") }
        .SelectMany(f => CssReader.Parse(File.ReadAllText(f)))
        .Where(b => b.Selector.StartsWith(":root", StringComparison.Ordinal))
        .SelectMany(b => b.Declarations).Where(d => baseNames.Contains(d.Key));
    Diff("base", baseExpected, CssReader.Parse(generated[$"{ThemeGenerator.CssFolder}/base.css"]).SelectMany(b => b.Declarations));

    Console.WriteLine(differences == 0
        ? $"All {set.Themes.Count} themes and the base block match the web client's CSS exactly."
        : $"{differences} difference(s).");
    return differences == 0 ? 0 : 1;
}

List<CssBlock> WebBlocks()
{
    var files = new List<string> { Need("colors") };
    files.AddRange(Directory.GetFiles(Need("themes"), "*.css").Order());
    return files.SelectMany(f => CssReader.Parse(File.ReadAllText(f))).ToList();
}

string Need(string option) =>
    options.TryGetValue(option, out var v) && v is not null ? v : throw new ArgumentException($"--{option} <path> is required.");

static Dictionary<string, string?> Options(string[] a)
{
    var d = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal);
        d[a[i][2..]] = hasValue ? a[++i] : null;
    }
    return d;
}

/// <summary>An entry in the web client's theme registry.</summary>
sealed record RegistryEntry(string Id, string Label, string Description, string Scheme, string Icon, bool HighContrast, string? Motto);

/// <summary>Reads the theme registry out of the web client's TypeScript, one <c>{ id: …, … }</c> entry at a time.</summary>
static class Registry
{
    public static List<RegistryEntry> Parse(string ts)
    {
        var start = ts.IndexOf("const REGISTRY", StringComparison.Ordinal);
        if (start < 0) throw new FormatException("No 'const REGISTRY' in the registry file.");
        var end = ts.IndexOf("\n]", start, StringComparison.Ordinal);
        var list = new List<RegistryEntry>();
        foreach (Match m in Regex.Matches(ts[start..end], @"\n  \{\r?\n(.*?)\r?\n  \}", RegexOptions.Singleline))
        {
            var e = m.Groups[1].Value;
            list.Add(new RegistryEntry(
                Str(e, "id")!, Str(e, "label")!, Str(e, "description") ?? "", Str(e, "scheme") ?? "dark",
                Str(e, "icon") ?? "mdi-palette-outline", Regex.IsMatch(e, @"highContrast:\s*true"), Str(e, "motto")));
        }
        return list;
    }

    private static string? Str(string text, string field)
    {
        var m = Regex.Match(text, @"\b" + field + @":\s*(?:'((?:[^'\\]|\\.)*)'|""((?:[^""\\]|\\.)*)"")");
        if (!m.Success) return null;
        var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return Regex.Unescape(v.Replace("\\'", "'"));
    }
}
