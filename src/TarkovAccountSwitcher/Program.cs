using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TarkovAccountSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Run the account switcher without administrator privileges.",
                AppBrand.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var user = identity.User?.Value ?? throw new InvalidOperationException("Windows user is unavailable.");
        var activate = args is [UpdateInstallations.ActivateArgument];
        var wait = activate || args is [UpdateInstallations.WaitArgument];
        using var instance = new Mutex(false, $@"Global\TarkovTracker.AccountSwitcher.{user}");
        if (!AcquireInstance(instance, wait))
        {
            MessageBox.Show("The account switcher is already running. Open it from the notification area.",
                AppBrand.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Run(args, activate);
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            MessageBox.Show("The switcher could not open its local data. Check access to the data folder and try again.",
                AppBrand.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static bool AcquireInstance(Mutex instance, bool wait)
    {
        try
        {
            return instance.WaitOne(wait ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void Run(string[] args, bool activate)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var data = Path.Combine(local, "TarkovTracker", "AccountSwitcher");
        var store = new VaultStore(Path.Combine(data, "vault.dat"));
        var settings = new LauncherSettings(Path.Combine(roaming, "Battlestate Games", "BsgLauncher", "settings"));
        var version = typeof(Program).Assembly.GetName().Version!;
        var current = new Version(version.Major, version.Minor, version.Build);
        var installations = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? new UpdateInstallations(data) : null;
        if (!activate && args is not [UpdateInstallations.SkipArgument] && installations?.TryStartActive(current) == true)
            return;

        using var client = installations is null ? null : new GitHubUpdates();
        using var main = new MainForm(new SwitchService(store, settings, new WindowsLauncher()), settings.Path,
            updates: client is null ? null : new AppUpdates(current, client, installations!));
        if (activate && installations is not null)
        {
            main.Shown += (_, _) =>
            {
                try
                {
                    if (!main.HasLoadedVault || !installations.ConfirmStarted(current, AppContext.BaseDirectory))
                        main.ReportUpdateIssue("This version started but could not be made the default.");
                }
                catch (Exception error) when (UpdateInstallations.IsExpected(error))
                {
                    main.ReportUpdateIssue("This version started but could not be made the default.");
                }
            };
        }
        Application.Run(main);
    }
}
