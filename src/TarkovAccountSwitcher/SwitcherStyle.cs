namespace TarkovAccountSwitcher;

internal static class SwitcherStyle
{
    private static readonly bool HighContrast = SystemInformation.HighContrast;
    internal static readonly Color Canvas = HighContrast ? SystemColors.Window : Color.FromArgb(245, 247, 250);
    internal static readonly Color Surface = HighContrast ? SystemColors.Window : Color.White;
    internal static readonly Color Text = HighContrast ? SystemColors.WindowText : Color.FromArgb(23, 35, 53);
    internal static readonly Color SecondaryText = HighContrast ? SystemColors.WindowText : Color.FromArgb(69, 84, 103);
    internal static readonly Color Border = HighContrast ? SystemColors.WindowText : Color.FromArgb(201, 210, 222);
    internal static readonly Color Info = HighContrast ? SystemColors.Window : Color.FromArgb(231, 239, 249);
    private static readonly Color Accent = Color.FromArgb(29, 70, 124);
    private static readonly Color SavedText = Color.FromArgb(19, 88, 66);
    private static readonly Color SignInText = Color.FromArgb(110, 69, 12);
    internal static readonly Brush SurfaceBrush = new SolidBrush(Surface);
    internal static readonly Brush SelectionBrush = new SolidBrush(HighContrast ? SystemColors.Highlight : Info);
    private static readonly Brush SavedBrush = new SolidBrush(Color.FromArgb(226, 244, 236));
    private static readonly Brush SignInBrush = new SolidBrush(Color.FromArgb(255, 242, 220));
    internal static readonly Pen BorderPen = new(HighContrast ? Border : Color.FromArgb(231, 236, 242));

    internal static Color RowText(bool selected) => HighContrast && selected ? SystemColors.HighlightText : Text;

    internal static Brush StatusBrush(bool saved, bool selected) =>
        HighContrast ? (selected ? SelectionBrush : SurfaceBrush) : saved ? SavedBrush : SignInBrush;

    internal static Color StatusText(bool saved, bool selected) =>
        HighContrast ? RowText(selected) : saved ? SavedText : SignInText;

    internal static void SetAccountRowHeight(ListBox list, int scaledPixels) =>
        list.ItemHeight = Math.Min(scaledPixels, 255);

    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(0, 35),
            Padding = new Padding(11, 4, 11, 4), Margin = Padding.Empty
        };
        if (HighContrast)
            return button;

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(38, 84, 145) : Info;
        button.ForeColor = primary ? Color.White : Text;
        button.BackColor = primary ? Accent : Surface;
        button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled && primary ? Accent : button.Enabled ? Surface : Canvas;
            button.FlatAppearance.BorderColor = button.Enabled && primary ? Accent : Border;
        };
        return button;
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
