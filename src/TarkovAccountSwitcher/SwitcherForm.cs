using System.Runtime.InteropServices;

namespace TarkovAccountSwitcher;

internal abstract class SwitcherForm : Form
{
    private const int WmSettingChange = 0x001A;
    private const int WmSysColorChange = 0x0015;
    private const int WmThemeChanged = 0x031A;
    private const int UseImmersiveDarkMode = 20;
    private bool themeRefreshQueued;

    protected SwitcherForm(Func<SwitcherPalette>? readPalette = null)
    {
        ReadPalette = readPalette ?? SwitcherPalette.FromSystem;
        Palette = ReadPalette();
    }

    protected Func<SwitcherPalette> ReadPalette { get; }
    protected SwitcherPalette Palette { get; private set; }

    protected virtual void ApplyPalette()
    {
        BackColor = Palette.Canvas;
        ForeColor = Palette.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshTheme();
        ApplyCaption();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is not (WmSettingChange or WmSysColorChange or WmThemeChanged) ||
            themeRefreshQueued || Disposing || !IsHandleCreated)
            return;

        themeRefreshQueued = true;
        BeginInvoke(() =>
        {
            themeRefreshQueued = false;
            if (!IsDisposed && !Disposing)
                RefreshTheme();
        });
    }

    private void RefreshTheme()
    {
        var palette = ReadPalette();
        if (palette == Palette)
            return;

        Palette = palette;
        ApplyPalette();
        ApplyCaption();
        Invalidate(invalidateChildren: true);
    }

    private void ApplyCaption()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var useDark = Palette.Dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, UseImmersiveDarkMode, in useDark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in int value, int valueSize);
}
