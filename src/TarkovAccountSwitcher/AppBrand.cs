namespace TarkovAccountSwitcher;

internal static class AppBrand
{
    internal const string Name = "Tarkov Relay";
    internal const string Title = Name + " — Account Switcher";

    internal static Icon WindowIcon { get; } = new(typeof(AppBrand), "Assets.TarkovRelay.ico");
    internal static Icon TrayIcon { get; } = new(WindowIcon, SystemInformation.SmallIconSize);
}
