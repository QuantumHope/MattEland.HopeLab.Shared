using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>A newer release that has an archive for this machine.</summary>
public sealed record UpdateOffer(ReleaseVersion Version, ReleaseVersion Current, string Tag, string ReleaseUrl, string? Notes,
    DateTimeOffset? PublishedAt, string Rid, ReleaseAsset Archive, GitHubReleaseAsset Download);

/// <summary>What a check found: <see cref="Offer"/> is null when this copy is up to date.</summary>
public sealed record UpdateCheck(ReleaseVersion Latest, ReleaseVersion Current, UpdateOffer? Offer, DateTimeOffset CheckedAt)
{
    public bool UpdateAvailable => Offer is not null;
}

/// <summary>Compares the running version with the latest release's <c>latest.json</c>.</summary>
public sealed class UpdateChecker(GitHubReleases releases, IClock clock)
{
    public async Task<Result<UpdateCheck>> CheckAsync(ReleaseVersion current, string rid, CancellationToken ct = default)
    {
        var latest = await releases.LatestAsync(ct).ConfigureAwait(false);
        if (!latest.IsSuccess) return Result<UpdateCheck>.Fail(latest.Error!);
        var release = latest.Value!;

        if (release.Asset(ReleaseManifest.FileName) is not { } manifestAsset)
            return Result<UpdateCheck>.Fail($"Release {release.Tag} has no {ReleaseManifest.FileName}.");
        var text = await releases.DownloadTextAsync(manifestAsset, ct).ConfigureAwait(false);
        if (!text.IsSuccess) return Result<UpdateCheck>.Fail(text.Error!);
        if (ReleaseManifest.Parse(text.Value!) is not { } manifest)
            return Result<UpdateCheck>.Fail($"Release {release.Tag} has an unreadable {ReleaseManifest.FileName}.");
        return Evaluate(manifest, release, current, rid, clock.UtcNow);
    }

    /// <summary>The decision on its own, so it can be tested without GitHub.</summary>
    internal static Result<UpdateCheck> Evaluate(ReleaseManifest manifest, GitHubRelease release, ReleaseVersion current, string rid,
        DateTimeOffset now)
    {
        if (!ReleaseVersion.TryParse(manifest.Version, out var latest))
            return Result<UpdateCheck>.Fail($"Release {release.Tag} names an unreadable version '{manifest.Version}'.");
        if (latest.Value <= current) return Result<UpdateCheck>.Ok(new UpdateCheck(latest.Value, current, null, now));

        if (!manifest.Assets.TryGetValue(rid, out var archive))
            return Result<UpdateCheck>.Fail($"Release {release.Tag} has no build for {rid}.");
        if (release.Asset(archive.Name) is not { } download)
            return Result<UpdateCheck>.Fail($"Release {release.Tag} lists {archive.Name} but doesn't have it.");

        var offer = new UpdateOffer(latest.Value, current, release.Tag, release.HtmlUrl, manifest.Notes ?? release.Body,
            manifest.PubDate ?? release.PublishedAt, rid, archive, download);
        return Result<UpdateCheck>.Ok(new UpdateCheck(latest.Value, current, offer, now));
    }

    /// <summary>
    /// The runtime identifier release archives are named for, or null where no build is published
    /// (only Windows and Linux on x64 today).
    /// </summary>
    public static string? CurrentRid()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null) return null;
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return null;
    }
}
