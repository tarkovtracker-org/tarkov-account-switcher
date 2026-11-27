using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TarkovAccountSwitcher;

internal sealed record UpdateInstallation(Guid Id, string Tag);

internal sealed record UpdateState(UpdateInstallation? Active = null,
    UpdateInstallation? Previous = null, UpdateInstallation? Pending = null);

internal sealed record AppUpdates(Version Current, GitHubUpdates Client, UpdateInstallations Installations);

internal sealed class UpdateInstallations(string dataDirectory)
{
    internal const string ActivateArgument = "--activate-update";
    internal const string WaitArgument = "--wait-for-switcher";
    internal const string SkipArgument = "--skip-update";
    private readonly string updates = Path.Combine(dataDirectory, "updates");
    private string Versions => Path.Combine(updates, "versions");
    private string StateFile => Path.Combine(updates, "state.json");
    private const string OwnershipFile = ".switcher-update";

    internal async Task<UpdateInstallation> StageAsync(GitHubUpdates client, UpdateRelease release,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(dataDirectory);
        if (!UpdatePackage.IsOrdinaryDirectory(dataDirectory))
            throw new IOException("The switcher's data directory is unavailable.");
        Directory.CreateDirectory(updates);
        if (!SafeDirectories())
            throw new IOException("The switcher's update directory is unavailable.");

        Directory.CreateDirectory(Versions);
        if (!UpdatePackage.IsOrdinaryDirectory(Versions))
            throw new IOException("The switcher's version directory is unavailable.");

        var install = new UpdateInstallation(Guid.NewGuid(), release.Tag);
        var stage = Path.Combine(Versions, $".stage-{install.Id:N}");
        var package = Path.Combine(stage, "release.zip");
        var app = Path.Combine(stage, "app");
        Directory.CreateDirectory(stage);
        var placed = false;
        try
        {
            await client.DownloadAsync(release, package, cancellation).ConfigureAwait(false);
            await UpdatePackage.ExtractAsync(package, app, cancellation).ConfigureAwait(false);
            if (!UpdatePackage.Matches(app, release.Version))
                throw new InvalidDataException("The ZIP does not contain the expected Windows x64 switcher.");

            using (File.Open(Path.Combine(app, OwnershipFile), FileMode.CreateNew))
            {
                // Identify app-owned folders for future cleanup.
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(app, Location(install));
            placed = true;
            var state = ReadState();
            WriteState(state with { Pending = install });
            Prune(state.Active, state.Previous, install);
            return install;
        }
        catch
        {
            if (placed)
                TryRemove(Location(install));
            throw;
        }
        finally
        {
            TryRemove(stage);
        }
    }

    internal void StartPending(UpdateInstallation install)
    {
        if (ReadState().Pending != install || !CanStart(install))
            throw new InvalidOperationException("The checked update is unavailable. Check again and retry.");

        Start(install, ActivateArgument);
    }

    internal bool TryStartActive(Version current)
    {
        var state = ReadState();
        foreach (var install in new[] { state.Active, state.Previous })
        {
            if (install is null || !GitHubUpdates.TryParseTag(install.Tag, out var version) || version <= current)
                continue;

            try
            {
                if (CanStart(install))
                {
                    Start(install, WaitArgument);
                    return true;
                }
            }
            catch (Exception error) when (IsExpected(error))
            {
                // Keep opening the existing switcher if a stored version cannot be launched.
            }
        }
        return false;
    }

    internal bool ConfirmStarted(Version current, string runningDirectory)
    {
        var state = ReadState();
        if (state.Pending is not { } pending || pending.Tag != $"v{current}" ||
            !string.Equals(Path.TrimEndingDirectorySeparator(runningDirectory), Location(pending), StringComparison.OrdinalIgnoreCase) ||
            !CanStart(pending))
            return false;

        WriteState(new UpdateState(pending, state.Active));
        Prune(pending, state.Active, null);
        return true;
    }

    internal string Location(UpdateInstallation install) => Path.Combine(Versions, install.Id.ToString("N"));

    internal static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or BadImageFormatException or Win32Exception or InvalidOperationException or HttpRequestException
            or OperationCanceledException or UriFormatException;

    private bool SafeDirectories() =>
        UpdatePackage.IsOrdinaryDirectory(dataDirectory) && UpdatePackage.IsOrdinaryDirectory(updates);

    private bool CanStart(UpdateInstallation install) =>
        install.Id != Guid.Empty && GitHubUpdates.TryParseTag(install.Tag, out var version) &&
        SafeDirectories() && UpdatePackage.IsOrdinaryDirectory(Versions) &&
        File.Exists(Path.Combine(Location(install), OwnershipFile)) &&
        UpdatePackage.Matches(Location(install), version);

    private UpdateState ReadState()
    {
        try
        {
            if (SafeDirectories() && File.Exists(StateFile))
            {
                using var file = File.Open(StateFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length <= 16 * 1024)
                    return JsonSerializer.Deserialize<UpdateState>(file) ?? new UpdateState();
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            // A missing or unreadable update record must not prevent ordinary startup.
        }
        return new UpdateState();
    }

    private void WriteState(UpdateState state) => AtomicFile.Write(StateFile, JsonSerializer.SerializeToUtf8Bytes(state));

    private void Start(UpdateInstallation install, string argument)
    {
        var directory = Location(install);
        var command = new ProcessStartInfo(Path.Combine(directory, UpdatePackage.Executable))
        {
            WorkingDirectory = directory,
            UseShellExecute = false
        };
        command.ArgumentList.Add(argument);
        using var process = Process.Start(command)
            ?? throw new InvalidOperationException("Windows did not open the updated switcher.");
    }

    private void Prune(UpdateInstallation? active, UpdateInstallation? previous, UpdateInstallation? pending)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Versions))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".stage-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name[7..], "N", out _))
                    TryRemove(directory);
                else if (Guid.TryParseExact(name, "N", out var id) &&
                    id != active?.Id && id != previous?.Id && id != pending?.Id &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), directory, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(directory, OwnershipFile)))
                    TryRemove(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cleanup can wait until a later successfully staged update.
        }
    }

    private void TryRemove(string directory)
    {
        try
        {
            if (SafeDirectories() && UpdatePackage.IsOrdinaryDirectory(Versions) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(Versions),
                    StringComparison.OrdinalIgnoreCase) && UpdatePackage.IsOrdinaryDirectory(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A running or locked version must never block activation.
        }
    }
}
