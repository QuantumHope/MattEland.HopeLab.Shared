using Avalonia.Controls;

namespace MattEland.HopeLab.Themes;

/// <summary>One shared theme: what a theme picker shows, and where its resource dictionary is.</summary>
/// <param name="Id">The theme's id, the same in every app and in the CSS (<c>data-theme</c>).</param>
/// <param name="Name">The name a picker shows.</param>
/// <param name="Description">One line under the name.</param>
/// <param name="FileName">The resource dictionary's file under <c>Themes/</c>.</param>
/// <param name="IsLight">Whether the theme is light, for apps that also switch Fluent's variant.</param>
/// <param name="HighContrast">Whether body text reaches 7:1 (WCAG AAA) on every surface.</param>
/// <param name="Icon">A Material Design icon name without the <c>mdi-</c> prefix (<c>car-sports</c>).</param>
/// <param name="Motto">A decorative line for the theme's chrome, or null.</param>
/// <param name="Create">Makes a new copy of the theme's resource dictionary.</param>
public sealed record ThemeInfo(
    string Id,
    string Name,
    string Description,
    string FileName,
    bool IsLight,
    bool HighContrast,
    string Icon,
    string? Motto,
    Func<ResourceDictionary> Create)
{
    /// <summary>The <c>avares://</c> address of the theme's resource dictionary.</summary>
    public Uri Source => new($"avares://MattEland.HopeLab.Themes/Themes/{FileName}");

    /// <summary>
    /// The icon as a Material.Icons kind name (<c>CarSports</c>), for apps that draw it with Material.Icons.Avalonia.
    /// </summary>
    public string IconKind => string.Concat(Icon.Split('-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>
    /// A new copy of the theme's resources, to add to an application's or a control's merged dictionaries. Compiled, so
    /// it is safe to trim and to compile ahead of time; <see cref="Source"/> is for a <c>ResourceInclude</c> in XAML.
    /// </summary>
    public ResourceDictionary CreateResources() => Create();
}
