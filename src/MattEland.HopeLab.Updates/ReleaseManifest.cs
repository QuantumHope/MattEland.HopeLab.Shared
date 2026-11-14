using System.Text.Json;
using System.Text.Json.Serialization;

namespace MattEland.HopeLab.Updates;

/// <summary>
/// <c>latest.json</c>, published as an asset of every release: what the release is and, per runtime identifier
/// (<c>win-x64</c>, <c>linux-x64</c>), which archive to download and its SHA-256. The same idea as the Tauri updater
/// manifest CORA's desktop app reads, with a hash in place of a minisign signature.
/// </summary>
public sealed record ReleaseManifest(
    string App,
    string Version,
    string Tag,
    string? Commit,
    DateTimeOffset? PubDate,
    string? Notes,
    IReadOnlyDictionary<string, ReleaseAsset> Assets)
{
    public const string FileName = "latest.json";

    public static ReleaseManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize(json, UpdatesJson.Default.ReleaseManifest); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, UpdatesJson.Default.ReleaseManifest);
}

/// <summary>One downloadable archive in a release: its asset file name, SHA-256 (hex) and size in bytes.</summary>
public sealed record ReleaseAsset(string Name, string Sha256, long Size);

/// <summary>
/// <c>release.json</c>, written beside the binaries of a published build. Its presence is what makes a copy updatable:
/// a build from source has none, so it never offers to replace itself. <paramref name="Package"/> says how the copy was
/// installed when that changes how it updates: null for a folder the user owns (archive, install script, per-user
/// setup), <c>"deb"</c> for a Debian package, which is updated by installing the next package.
/// </summary>
public sealed record ReleaseStamp(string App, string Version, string Rid, string Repository, string? Commit, DateTimeOffset? BuiltAt,
    string? Package = null)
{
    public const string FileName = "release.json";
    public const string DebPackage = "deb";

    /// <summary>The <c>latest.json</c> asset key for this copy: the RID, plus <c>.deb</c> for a Debian package.</summary>
    public string AssetKey => Package is { Length: > 0 } p ? $"{Rid}.{p}" : Rid;

    /// <summary>The stamp in <paramref name="directory"/>, or null when there is none or it can't be read.</summary>
    public static ReleaseStamp? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), UpdatesJson.Default.ReleaseStamp) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, UpdatesJson.Default.ReleaseStamp);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(ReleaseStamp))]
internal sealed partial class UpdatesJson : JsonSerializerContext;
