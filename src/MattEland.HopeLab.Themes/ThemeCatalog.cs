namespace MattEland.HopeLab.Themes;

/// <summary>
/// The shared themes. <see cref="All"/> and <see cref="DefaultId"/> are generated from the token files
/// (<c>tokens/themes.json</c>); an app adds its own themes beside these rather than editing them.
/// </summary>
public static partial class ThemeCatalog
{
    /// <summary>The theme with this id, or null.</summary>
    public static ThemeInfo? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>The theme with this id, or the default theme when there is none.</summary>
    public static ThemeInfo FindOrDefault(string? id) => Find(id) ?? Find(DefaultId)!;
}
