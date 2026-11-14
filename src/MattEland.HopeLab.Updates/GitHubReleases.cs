using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>A published GitHub release, as much of it as an update needs.</summary>
public sealed record GitHubRelease(string Tag, string HtmlUrl, DateTimeOffset? PublishedAt, string? Body,
    IReadOnlyList<GitHubReleaseAsset> Assets)
{
    public GitHubReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A release asset. <see cref="ApiUrl"/> is the download address that works for private repositories too.</summary>
public sealed record GitHubReleaseAsset(long Id, string Name, long Size, string ApiUrl, string BrowserUrl);

/// <summary>
/// Reads releases through the GitHub REST API. Assets are fetched from their API address with
/// <c>Accept: application/octet-stream</c>, which works for a private repository given a token (the plain
/// <c>releases/latest/download/…</c> link does not); the redirect to storage drops the token, as it should.
/// </summary>
public sealed class GitHubReleases
{
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly Func<string?> _token;
    private readonly string _userAgent;

    /// <param name="repository"><c>owner/name</c>.</param>
    /// <param name="token">Called once per request, so a sign-in made while the app runs is picked up.</param>
    public GitHubReleases(HttpClient http, string repository, Func<string?> token, string userAgent = "MattEland.HopeLab.Updates")
    {
        _http = http;
        _repository = repository;
        _token = token;
        _userAgent = userAgent;
    }

    public string Repository => _repository;

    /// <summary>How long a download may receive nothing before it is given up as stalled.</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private const string NoReleasesVisible = "No releases visible for";
    private const string TokenRejected = "GitHub rejected the token";

    /// <summary>
    /// Whether a failure is about access (a private repository with no token, a rejected token) rather than the network
    /// or GitHub being unavailable: the first won't fix itself and is worth telling someone about; the second is.
    /// </summary>
    public static bool IsAccessProblem(string? error) =>
        error is not null && (error.StartsWith(NoReleasesVisible, StringComparison.Ordinal) || error.StartsWith(TokenRejected, StringComparison.Ordinal));

    /// <summary>The release GitHub marks Latest (never a draft or pre-release).</summary>
    public async Task<Result<GitHubRelease>> LatestAsync(CancellationToken ct = default)
    {
        var token = _token();
        using var request = Request(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<GitHubRelease>.Fail(Explain(response.StatusCode, token is not null));
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, GitHubJson.Default.ReleaseDto);
            if (dto?.TagName is not { Length: > 0 }) return Result<GitHubRelease>.Fail("GitHub's answer had no release in it.");
            return Result<GitHubRelease>.Ok(new GitHubRelease(dto.TagName, dto.HtmlUrl ?? "", dto.PublishedAt, dto.Body,
                [.. (dto.Assets ?? []).Select(a => new GitHubReleaseAsset(a.Id, a.Name ?? "", a.Size, a.Url ?? "", a.BrowserDownloadUrl ?? ""))]));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return Result<GitHubRelease>.Fail($"Couldn't reach GitHub: {e.Message}");
        }
    }

    /// <summary>
    /// Whether the token can read the repository's releases. <see cref="LatestAsync"/> answers 404 both for "no release
    /// yet" and for "this token can't read releases", and a token that can see the repository (Metadata) may still lack
    /// Contents; listing releases needs exactly what downloading does, and answers 200 with an empty list when there are
    /// none. Ok(false) is "can't read them"; a failure is GitHub being unreachable or the token rejected.
    /// </summary>
    public async Task<Result<bool>> CanReadReleasesAsync(CancellationToken ct = default)
    {
        var token = _token();
        using var request = Request(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases?per_page=1", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return Result<bool>.Ok(true);
            // 404 (a private repository this token can't see) and 403 (it can, without Contents): no releases for this token.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return Result<bool>.Ok(false);
            return Result<bool>.Fail(Explain(response.StatusCode, token is not null));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result<bool>.Fail($"Couldn't reach GitHub: {e.Message}");
        }
    }

    /// <summary>Downloads an asset to <paramref name="destination"/>, reporting bytes written.</summary>
    public async Task<Result<string>> DownloadAsync(GitHubReleaseAsset asset, string destination, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var token = _token();
        using var request = Request(HttpMethod.Get, asset.ApiUrl, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var partial = destination + ".partial";
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<string>.Fail(Explain(response.StatusCode, token is not null));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    // HttpClient.Timeout stops at the headers; a body that stalls would wait for ever without this.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(ReadIdleTimeout);
                    int read;
                    try { read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return Result<string>.Fail($"Downloading {asset.Name} stalled (nothing for {ReadIdleTimeout.TotalSeconds:0} s). Try again.");
                    }
                    if (read == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }
            File.Move(partial, destination, overwrite: true);
            return Result<string>.Ok(destination);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result<string>.Fail($"Couldn't download {asset.Name}: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        }
    }

    /// <summary>Downloads a small text asset (the manifest) into memory.</summary>
    public async Task<Result<string>> DownloadTextAsync(GitHubReleaseAsset asset, CancellationToken ct = default)
    {
        var token = _token();
        using var request = Request(HttpMethod.Get, asset.ApiUrl, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result<string>.Fail(Explain(response.StatusCode, token is not null));
            return Result<string>.Ok(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result<string>.Fail($"Couldn't download {asset.Name}: {e.Message}");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// GitHub answers 404, not 401, for a private repository it won't show you, so that case gets the sign-in advice.
    /// </summary>
    internal string Explain(HttpStatusCode status, bool hadToken) => status switch
    {
        HttpStatusCode.NotFound when !hadToken =>
            $"{NoReleasesVisible} {_repository}. It is private: save a GitHub token that can read it, or sign in with `gh auth login`.",
        HttpStatusCode.NotFound => $"{_repository} has no published release yet, or this token can't read it.",
        HttpStatusCode.Unauthorized => $"{TokenRejected}: it may have expired or been revoked. Save a new one, or sign in with `gh auth login`.",
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
            "GitHub refused the request (rate limit or permissions). Try again later, or sign in with `gh auth login`.",
        _ => $"GitHub answered {(int)status} {status}.",
    };
}

internal sealed record ReleaseDto(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] List<AssetDto>? Assets);

internal sealed record AssetDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

[JsonSerializable(typeof(ReleaseDto))]
internal sealed partial class GitHubJson : JsonSerializerContext;
