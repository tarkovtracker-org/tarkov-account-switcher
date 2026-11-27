using System.Security;
using Microsoft.Win32;

namespace TarkovAccountSwitcher;

internal static class LauncherDiscovery
{
    // The standalone launcher's Inno Setup registration, including on 64-bit Windows.
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B0FDA062-7581-4D67-B085-C4E7C358037F}_is1";

    internal static string? Find()
    {
        var paths = new[] { ReadInstallation(RegistryView.Registry32), ReadInstallation(RegistryView.Registry64) }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length == 1 ? paths[0] : null;
    }

    private static string? ReadInstallation(RegistryView view)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var installation = machine.OpenSubKey(UninstallKey);
            if (installation?.GetValue("Publisher") is not "Battlestate Games" ||
                installation.GetValue("InstallLocation") is not string directory ||
                !Path.IsPathFullyQualified(directory))
                return null;

            var path = Path.GetFullPath(Path.Combine(directory, WindowsLauncher.Executable));
            return File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }
    }
}
