using System.Security;
using Microsoft.Win32;

namespace TarkovAccountSwitcher;

internal sealed record SwitcherPalette(
    Color Canvas, Color Surface, Color Text, Color SecondaryText, Color Border, Color Info,
    Color Accent, Color AccentText, Color AccentHover, Color Selection, Color SelectionText,
    Color Saved, Color SavedText, Color SignIn, Color SignInText, bool Dark = false, bool HighContrast = false)
{
    private static readonly SwitcherPalette Light = new(
        Color.FromArgb(245, 247, 250), Color.White, Color.FromArgb(23, 35, 53), Color.FromArgb(69, 84, 103),
        Color.FromArgb(201, 210, 222), Color.FromArgb(231, 239, 249),
        Color.FromArgb(29, 70, 124), Color.White, Color.FromArgb(38, 84, 145),
        Color.FromArgb(231, 239, 249), Color.FromArgb(23, 35, 53),
        Color.FromArgb(226, 244, 236), Color.FromArgb(19, 88, 66),
        Color.FromArgb(255, 242, 220), Color.FromArgb(110, 69, 12));

    private static readonly SwitcherPalette DarkPalette = new(
        Color.FromArgb(23, 27, 35), Color.FromArgb(32, 38, 49), Color.FromArgb(234, 239, 247), Color.FromArgb(179, 191, 208),
        Color.FromArgb(66, 79, 97), Color.FromArgb(39, 60, 87),
        Color.FromArgb(145, 190, 255), Color.FromArgb(11, 28, 52), Color.FromArgb(174, 208, 255),
        Color.FromArgb(43, 65, 95), Color.FromArgb(234, 239, 247),
        Color.FromArgb(34, 66, 57), Color.FromArgb(171, 229, 199),
        Color.FromArgb(68, 52, 37), Color.FromArgb(248, 213, 159), Dark: true);

    internal static SwitcherPalette FromSystem()
    {
        if (SystemInformation.HighContrast)
            return FromPreferences(dark: false, highContrast: true);

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return FromPreferences(Application.SystemColorMode == SystemColorMode.Dark, highContrast: false);

        // WinForms only exposes the Windows 11 dark mode. Read the app preference on older Windows.
        try
        {
            var setting = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return FromPreferences(setting is 0, highContrast: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Light;
        }
    }

    internal static SwitcherPalette FromPreferences(bool dark, bool highContrast)
    {
        if (!highContrast)
            return dark ? DarkPalette : Light;

        return new SwitcherPalette(SystemColors.Window, SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
            SystemColors.WindowText, SystemColors.Window, SystemColors.Highlight, SystemColors.HighlightText,
            SystemColors.Highlight, SystemColors.Highlight, SystemColors.HighlightText, SystemColors.Window,
            SystemColors.WindowText, SystemColors.Window, SystemColors.WindowText, HighContrast: true);
    }

    internal (Color Back, Color Fore) SessionColors(bool saved, bool selected)
    {
        if (HighContrast && selected)
            return (Selection, SelectionText);
        return saved ? (Saved, SavedText) : (SignIn, SignInText);
    }
}
