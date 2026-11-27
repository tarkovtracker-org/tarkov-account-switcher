using System.Security.Principal;

namespace TarkovAccountSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Run the account switcher without administrator privileges.",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var user = identity.User?.Value ?? throw new InvalidOperationException("Windows user is unavailable.");
        using var instance = new Mutex(true, $@"Global\TarkovTracker.AccountSwitcher.{user}", out var first);
        if (!first)
        {
            MessageBox.Show("The account switcher is already running. Open it from the notification area.",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var store = new VaultStore(Path.Combine(local, "TarkovTracker", "AccountSwitcher", "vault.dat"));
        var settings = new LauncherSettings(Path.Combine(roaming, "Battlestate Games", "BsgLauncher", "settings"));
        try
        {
            using var main = new MainForm(new SwitchService(store, settings, new WindowsLauncher()), settings.Path);
            Application.Run(main);
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            MessageBox.Show("The switcher could not open its local data. Check access to the data folder and try again.",
                MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
