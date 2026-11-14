using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>
/// Gets an offered release ready to install: downloads the archive into a work folder, checks its SHA-256 against
/// the manifest, and unpacks it into a staging folder whose contents mirror the install folder.
/// </summary>
public static class UpdatePackage
{
    /// <returns>The staging folder.</returns>
    public static async Task<Result<string>> PrepareAsync(GitHubReleases releases, UpdateOffer offer, string workDirectory,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var archive = await DownloadVerifiedAsync(releases, offer, workDirectory, progress, ct).ConfigureAwait(false);
        if (!archive.IsSuccess) return archive;
        return Extract(archive.Value!, Path.Combine(Path.GetDirectoryName(archive.Value!)!, "staging"));
    }

    /// <summary>
    /// Downloads the offered file into <c>&lt;work&gt;/&lt;version&gt;/</c> and checks its SHA-256, without unpacking it
    /// (a package the system installs, say). Returns the file's path.
    /// </summary>
    public static async Task<Result<string>> DownloadVerifiedAsync(GitHubReleases releases, UpdateOffer offer, string workDirectory,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var archive = Path.Combine(workDirectory, offer.Version.ToString(), offer.Archive.Name);

        // A finished download that still matches is reused; anything else is fetched again.
        if (File.Exists(archive) && await MatchesAsync(archive, offer.Archive.Sha256, ct).ConfigureAwait(false))
            return Result<string>.Ok(archive);
        var downloaded = await releases.DownloadAsync(offer.Download, archive, progress, ct).ConfigureAwait(false);
        if (!downloaded.IsSuccess) return downloaded;
        if (await MatchesAsync(archive, offer.Archive.Sha256, ct).ConfigureAwait(false)) return Result<string>.Ok(archive);
        TryDelete(archive);
        return Result<string>.Fail($"{offer.Archive.Name} didn't match the checksum in {ReleaseManifest.FileName}; it was deleted.");
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken ct) =>
        string.Equals(await Sha256Async(path, ct).ConfigureAwait(false), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Unpacks a <c>.zip</c> or <c>.tar.gz</c> into a fresh <paramref name="staging"/> folder.</summary>
    public static Result<string> Extract(string archive, string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, staging);
            else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                // tar keeps the execute bits a zip would lose, which is why Linux builds ship as tar.gz.
                using var file = File.OpenRead(archive);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
            }
            else return Result<string>.Fail($"Don't know how to unpack {Path.GetFileName(archive)}.");
            return Result<string>.Ok(staging);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result<string>.Fail($"Couldn't unpack {Path.GetFileName(archive)}: {e.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
