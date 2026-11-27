using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovAccountSwitcher;

internal sealed record UpdateRelease(Version Version, Uri Download, long Bytes, byte[] Digest)
{
    internal string Tag => $"v{Version}";
}

internal sealed class GitHubUpdates : IDisposable
{
    private const string Repository = "tarkovtracker-org/tarkov-account-switcher";
    private const long MaxDownloadBytes = 200L * 1024 * 1024;
    private const int MaxMetadataBytes = 512 * 1024;
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal GitHubUpdates(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovAccountSwitcher");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxMetadataBytes, timeout.Token).ConfigureAwait(false);
        var data = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<ReleaseMetadata>(data, Json, timeout.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release.");

        if (release.Draft || release.Prerelease || !TryParseTag(release.Tag, out var version) || version <= current)
            return null;

        var name = $"TarkovAccountSwitcher-{release.Tag}-win-x64.zip";
        var download = new Uri($"https://github.com/{Repository}/releases/download/{release.Tag}/{name}");
        var asset = release.Assets?.FirstOrDefault(asset => asset.Name == name && asset.State == "uploaded");
        if (asset is null || asset.Size is <= 0 or > MaxDownloadBytes || asset.Download != download.AbsoluteUri ||
            asset.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest.AsSpan(7).ContainsOnlyHex())
            throw new InvalidDataException("The release is missing a verifiable Windows x64 ZIP.");

        return new UpdateRelease(version, download, asset.Size, Convert.FromHexString(digest.AsSpan(7)));
    }

    internal async Task DownloadAsync(UpdateRelease release, string destination, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var uri = release.Download;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!IsDownloadHost(uri))
                throw new InvalidDataException("GitHub redirected the update to an unexpected address.");

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } bytes && bytes != release.Bytes)
                throw new InvalidDataException("The update download size does not match the release.");

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyExactAsync(input, output, release.Bytes, timeout.Token, hash).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Digest))
                throw new InvalidDataException("The update download did not match GitHub's SHA-256 digest.");
            return;
        }

        throw new InvalidDataException("GitHub redirected the update too many times.");
    }

    internal static bool TryParseTag(string? tag, out Version version)
    {
        if (tag is { Length: >= 6 and <= 35 } && tag[0] == 'v' &&
            Version.TryParse(tag[1..], out var parsed) && parsed.Build >= 0 && parsed.Revision == -1 &&
            tag == $"v{parsed}")
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    internal static async Task CopyExactAsync(Stream input, Stream output, long expected,
        CancellationToken cancellation, IncrementalHash? hash = null)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        {
            if (read > expected - written)
                throw new InvalidDataException("The update contains more data than expected.");
            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            written += read;
        }

        if (written != expected)
            throw new InvalidDataException("The update download is incomplete.");
    }

    private static bool IsDownloadHost(Uri uri) =>
        uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    public void Dispose() => http.Dispose();

    private sealed record ReleaseMetadata([property: JsonPropertyName("tag_name")] string? Tag,
        bool Draft, bool Prerelease, ReleaseAsset[]? Assets);

    private sealed record ReleaseAsset(string? Name, string? State, long Size, string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Download);
}

internal static class HexCharacters
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }
        return true;
    }
}
