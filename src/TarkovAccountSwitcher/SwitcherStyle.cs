namespace TarkovAccountSwitcher;

internal static class SwitcherStyle
{
    internal static void SetAccountRowHeight(ListBox list, int scaledPixels) =>
        list.ItemHeight = Math.Min(scaledPixels, 255);

    internal static SwitcherButton Button(string text, bool primary = false) =>
        new(primary)
        {
            Text = text, AutoSize = true, MinimumSize = new Size(0, 35),
            Padding = new Padding(11, 4, 11, 4), Margin = Padding.Empty
        };

    internal static void StyleMenu(ContextMenuStrip menu, SwitcherPalette palette)
    {
        menu.BackColor = palette.Surface;
        menu.ForeColor = palette.Text;
        if (palette.HighContrast)
            menu.RenderMode = ToolStripRenderMode.System;
        else
            menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(palette)) { RoundedEdges = false };
    }

    private sealed class MenuColors(SwitcherPalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Surface;
        public override Color MenuBorder => palette.Border;
        public override Color MenuItemBorder => palette.Border;
        public override Color MenuItemSelected => palette.Selection;
        public override Color SeparatorDark => palette.Border;
        public override Color SeparatorLight => palette.Surface;
        public override Color ImageMarginGradientBegin => palette.Surface;
        public override Color ImageMarginGradientMiddle => palette.Surface;
        public override Color ImageMarginGradientEnd => palette.Surface;
    }

    internal static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty
        };
        foreach (var button in buttons)
        {
            button.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(button);
        }
        return row;
    }

    internal static TableLayoutPanel Split(Control left, Control right, Padding margin)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = margin
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        right.Anchor = AnchorStyles.Right;
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }
}

internal sealed class SwitcherButton(bool primary) : Button
{
    private SwitcherPalette? palette;

    internal void ApplyPalette(SwitcherPalette theme)
    {
        palette = theme;
        FlatStyle = theme.HighContrast ? FlatStyle.Standard : FlatStyle.Flat;
        UseVisualStyleBackColor = theme.HighContrast;
        ApplyColors();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyColors();
    }

    private void ApplyColors()
    {
        if (palette is not { } theme)
            return;

        if (theme.HighContrast)
        {
            BackColor = Color.Empty;
            ForeColor = Color.Empty;
            UseVisualStyleBackColor = true;
            return;
        }

        var accented = Enabled && primary;
        ForeColor = accented ? theme.AccentText : theme.Text;
        BackColor = accented ? theme.Accent : Enabled ? theme.Surface : theme.Canvas;
        FlatAppearance.BorderColor = accented ? theme.Accent : theme.Border;
        FlatAppearance.MouseOverBackColor = primary ? theme.AccentHover : theme.Info;
    }
}
