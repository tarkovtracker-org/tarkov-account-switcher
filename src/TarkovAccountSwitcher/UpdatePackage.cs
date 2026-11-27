using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;

namespace TarkovAccountSwitcher;

internal static class UpdatePackage
{
    internal const string Executable = "TarkovAccountSwitcher.exe";
    private const string Assembly = "TarkovAccountSwitcher.dll";
    private const string PackageRoot = "TarkovAccountSwitcher/";
    private const long MaxExtractedBytes = 350L * 1024 * 1024;
    private static readonly string[] RequiredFiles =
        [Executable, Assembly, "TarkovAccountSwitcher.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "System.Windows.Forms.dll"];

    internal static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellation)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > 2500)
            throw new InvalidDataException("The update ZIP has an unexpected number of entries.");

        var root = Path.GetFullPath(destination);
        if (!Path.EndsInDirectorySeparator(root))
            root += Path.DirectorySeparatorChar;
        long remaining = MaxExtractedBytes;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name == PackageRoot)
                continue;
            if (!name.StartsWith(PackageRoot, StringComparison.Ordinal) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("The update ZIP has an unexpected entry.");

            var directory = name.EndsWith('/');
            var path = name[PackageRoot.Length..].TrimEnd('/').Split('/');
            if (path.Any(part => !IsAllowedName(part)))
                throw new InvalidDataException("The update ZIP contains an invalid path.");
            var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(path)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update ZIP contains an invalid path.");
            if (directory)
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            if (entry.Length > remaining)
                throw new InvalidDataException("The extracted update exceeds its size limit.");
            remaining -= entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var input = entry.Open();
            await using var output = File.Open(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await GitHubUpdates.CopyExactAsync(input, output, entry.Length, cancellation).ConfigureAwait(false);
        }
    }

    internal static bool Matches(string directory, Version expected)
    {
        if (!IsOrdinaryDirectory(directory) ||
            RequiredFiles.Any(name => !IsOrdinaryFile(Path.Combine(directory, name))))
            return false;

        var info = FileVersionInfo.GetVersionInfo(Path.Combine(directory, Assembly));
        using var executable = File.OpenRead(Path.Combine(directory, Executable));
        using var reader = new PEReader(executable);
        return info.FileMajorPart == expected.Major && info.FileMinorPart == expected.Minor &&
            info.FileBuildPart == expected.Build && info.FilePrivatePart == 0 &&
            reader.PEHeaders.CoffHeader.Machine == Machine.Amd64;
    }

    internal static bool IsOrdinaryDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool IsOrdinaryFile(string path) =>
        File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool IsAllowedName(string name)
    {
        if (name.Length is 0 or > 150 || name.EndsWith('.') ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            return false;

        var stem = name.Split('.', 2)[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]));
    }
}
