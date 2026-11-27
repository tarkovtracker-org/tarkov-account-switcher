using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovAccountSwitcher;

internal static class UpdateTests
{
    private const string Repository = "https://github.com/tarkovtracker-org/tarkov-account-switcher";

    internal static IEnumerable<(string Name, Func<Fixture, Task> Run)> Cases =>
    [
        ("Updater only accepts a newer stable release with a verified Windows asset", CheckReleases),
        ("Updater rejects redirects outside the GitHub release hosts", RejectRedirect),
        ("Updater rejects a wrong digest and a truncated download", RejectBadDownloads),
        ("Updater rejects an archive entry outside the release folder", RejectOutsideEntry),
        ("Updater stages beside the previous version and waits for matching startup", StageAndActivate),
        ("Updater rejects a package with a mismatched version", RejectWrongVersion)
    ];

    private static async Task CheckReleases(Fixture fixture)
    {
        var release = Release(new Version(1, 4, 0), [9, 2, 3]);
        using var github = new GitHubUpdates(new ScriptedHttp(_ => Metadata(release)));
        var result = await github.CheckAsync(new Version(1, 3, 0), default);
        Require(result?.Version == release.Version && result.Download == release.Download &&
            result.Digest.SequenceEqual(release.Digest), "valid release not reported");
        Require(await github.CheckAsync(release.Version, default) is null, "same release reported");
        Require(await github.CheckAsync(new Version(2, 0, 0), default) is null, "downgrade reported");

        using var preview = new GitHubUpdates(new ScriptedHttp(_ => Metadata(release, prerelease: true)));
        Require(await preview.CheckAsync(new Version(1, 3, 0), default) is null, "prerelease reported");
        using var invalid = new GitHubUpdates(new ScriptedHttp(_ => Metadata(release, overrideDigest: "bad")));
        await Reject(() => invalid.CheckAsync(new Version(1, 3, 0), default));
        using var foreign = new GitHubUpdates(new ScriptedHttp(_ => Metadata(release, overrideUrl: "https://example.test/file.zip")));
        await Reject(() => foreign.CheckAsync(new Version(1, 3, 0), default));
    }

    private static async Task RejectRedirect(Fixture fixture)
    {
        var release = Release(new Version(1, 4, 0), [9, 2, 3]);
        var requests = 0;
        using var github = new GitHubUpdates(new ScriptedHttp(_ =>
        {
            requests++;
            if (requests != 1)
                throw new Exception("Unexpected redirect request");
            return new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://example.test/update.zip") }
            };
        }));
        await Reject(() => github.DownloadAsync(release, Path.Combine(Root(fixture), "redirect.zip"), default));
        Require(requests == 1, "followed an untrusted redirect");
    }

    private static async Task RejectBadDownloads(Fixture fixture)
    {
        byte[] bytes = [9, 2, 3];
        var release = Release(new Version(1, 4, 0), bytes);
        using var github = Client(bytes);
        var directory = Root(fixture);
        await Reject(() => github.DownloadAsync(release with { Digest = new byte[32] }, Path.Combine(directory, "digest.zip"), default));

        byte[] shortBytes = [bytes[0], bytes[1]];
        using var truncated = new GitHubUpdates(new ScriptedHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(shortBytes)
            {
                Headers = { ContentLength = release.Bytes }
            }
        }));
        await Reject(() => truncated.DownloadAsync(release with { Digest = SHA256.HashData(shortBytes) },
            Path.Combine(directory, "truncated.zip"), default));
    }

    private static async Task RejectOutsideEntry(Fixture fixture)
    {
        var directory = Root(fixture);
        var archive = Path.Combine(directory, "bad.zip");
        File.WriteAllBytes(archive, Bundle("TarkovAccountSwitcher/../stray.txt"));
        await Reject(() => UpdatePackage.ExtractAsync(archive, Path.Combine(directory, "only-here"), default));
        Require(!File.Exists(Path.Combine(directory, "stray.txt")), "archive escaped its destination");

        File.WriteAllBytes(archive, Bundle("TarkovAccountSwitcher/unsafe .dll"));
        await Reject(() => UpdatePackage.ExtractAsync(archive, Path.Combine(directory, "only-ordinary-names"), default));
    }

    private static async Task StageAndActivate(Fixture fixture)
    {
        var directory = Root(fixture);
        var versions = new UpdateInstallations(directory);
        var appVersion = AppVersion();
        var bundle = Bundle();
        var release = Release(appVersion, bundle);
        using var github = Client(bundle);
        var first = await versions.StageAsync(github, release, default);
        Require(State(directory).Pending == first && State(directory).Active is null, "activated before startup");
        Require(!versions.ConfirmStarted(appVersion, directory), "activated from the wrong directory");
        Require(versions.ConfirmStarted(appVersion, versions.Location(first)), "valid startup wasn't recorded");

        var second = await versions.StageAsync(github, release, default);
        Require(State(directory).Active == first && State(directory).Pending == second, "replaced last good version while staging");
        Require(!versions.ConfirmStarted(new Version(0, 1, 0), versions.Location(second)), "activated a different version");
        Require(versions.ConfirmStarted(appVersion, versions.Location(second)), "second startup wasn't recorded");
        Require(State(directory) == new UpdateState(second, first) &&
            Directory.Exists(versions.Location(first)), "previous working version wasn't retained");
    }

    private static async Task RejectWrongVersion(Fixture fixture)
    {
        var directory = Root(fixture);
        var versions = new UpdateInstallations(directory);
        var bundle = Bundle();
        using var github = Client(bundle);
        await Reject(() => versions.StageAsync(github, Release(new Version(9, 0, 0), bundle), default));
        Require(!File.Exists(Path.Combine(directory, "updates", "state.json")) &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(directory, "updates", "versions")).Any(),
            "rejected package left a runnable installation");
    }

    private static GitHubUpdates Client(byte[] bytes) =>
        new(new ScriptedHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

    private static HttpResponseMessage Metadata(UpdateRelease release, bool prerelease = false,
        string? overrideDigest = null, string? overrideUrl = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = release.Tag, draft = false, prerelease,
                assets = new[] { new
                {
                    name = $"TarkovAccountSwitcher-{release.Tag}-win-x64.zip", state = "uploaded",
                    size = release.Bytes, browser_download_url = overrideUrl ?? release.Download.AbsoluteUri,
                    digest = overrideDigest ?? $"sha256:{Convert.ToHexString(release.Digest)}"
                } }
            }))
        };

    private static UpdateRelease Release(Version version, byte[] bytes) =>
        new(version, new Uri($"{Repository}/releases/download/v{version}/TarkovAccountSwitcher-v{version}-win-x64.zip"),
            bytes.Length, SHA256.HashData(bytes));

    private static Version AppVersion()
    {
        var version = typeof(UpdateInstallations).Assembly.GetName().Version!;
        return new Version(version.Major, version.Minor, version.Build);
    }

    private static byte[] Bundle(string? extra = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var directory = Path.GetDirectoryName(typeof(UpdateInstallations).Assembly.Location)!;
            foreach (var name in new[] { UpdatePackage.Executable, "TarkovAccountSwitcher.dll",
                "TarkovAccountSwitcher.runtimeconfig.json" })
                zip.CreateEntryFromFile(Path.Combine(directory, name), $"TarkovAccountSwitcher/{name}");
            foreach (var name in new[] { "coreclr.dll", "hostfxr.dll", "System.Windows.Forms.dll" })
                zip.CreateEntry($"TarkovAccountSwitcher/{name}");
            if (extra is not null)
                zip.CreateEntry(extra);
        }
        return output.ToArray();
    }

    private static string Root(Fixture fixture) => Path.GetDirectoryName(fixture.VaultPath)!;

    private static UpdateState State(string directory) =>
        JsonSerializer.Deserialize<UpdateState>(File.ReadAllBytes(Path.Combine(directory, "updates", "state.json")))!;

    private static void Require(bool condition, string detail)
    {
        if (!condition)
            throw new Exception(detail);
    }

    private static async Task Reject(Func<Task> task)
    {
        try
        {
            await task();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new Exception("Expected the updater to reject malformed input.");
    }

    private sealed class ScriptedHttp(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
