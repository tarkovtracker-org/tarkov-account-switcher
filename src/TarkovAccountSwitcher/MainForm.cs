using System.Net.Mail;

namespace TarkovAccountSwitcher;

internal sealed class MainForm : Form
{
    internal const string AppTitle = "Tarkov Account Switcher";
    private readonly SwitchService service;
    private readonly string settingsPath;
    private readonly ListBox accounts = new()
    {
        Dock = DockStyle.Fill, AccessibleName = "Saved accounts", BorderStyle = BorderStyle.None,
        IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, Margin = Padding.Empty
    };
    private readonly Label emptyAccounts = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label path = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 22, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "Switch status" };
    private readonly Button choose = SwitcherStyle.Button("Choose launcher…");
    private readonly Button switchAccount = SwitcherStyle.Button("Switch account", primary: true);
    private readonly Button add = SwitcherStyle.Button("Add account…");
    private readonly Button signIn = SwitcherStyle.Button("Sign in again");
    private readonly Button remove = SwitcherStyle.Button("Remove…");
    private readonly Button restore = SwitcherStyle.Button("Restore previous settings", primary: true);
    private readonly Button keep = SwitcherStyle.Button("Keep current settings…");
    private readonly Font headingFont = new("Segoe UI", 21F, FontStyle.Bold);
    private readonly Font accountFont = new("Segoe UI", 10F, FontStyle.Bold);
    private readonly Font bodyFont = new("Segoe UI", 10F);
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = AppTitle };
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer capture = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer expiry = new() { Interval = 300_000 };
    private FileSystemWatcher? watcher;
    private Vault vault = Vault.Empty;
    private Guid watchedAccount;
    private int captureAttempts;
    private bool busy;
    private bool exiting;
    private bool loaded;

    internal MainForm(SwitchService service, string settingsPath)
    {
        this.service = service;
        this.settingsPath = settingsPath;
        Text = AppTitle;
        Icon = SystemIcons.Application;
        ClientSize = new Size(680, 465);
        MinimumSize = new Size(600, 415);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SwitcherStyle.Canvas;
        ForeColor = SwitcherStyle.Text;
        Font = bodyFont;
        Controls.Add(BuildLayout());

        accounts.DrawItem += DrawAccount;
        SwitcherStyle.SetAccountRowHeight(accounts, Pixels(54));
        DpiChanged += (_, _) => SwitcherStyle.SetAccountRowHeight(accounts, Pixels(54));
        AcceptButton = switchAccount;

        choose.Click += (_, _) => ChooseLauncher();
        add.Click += async (_, _) => await AddAccount();
        remove.Click += (_, _) => RemoveAccount();
        switchAccount.Click += async (_, _) => await SwitchSelected();
        accounts.DoubleClick += async (_, _) => await SwitchSelected();
        signIn.Click += async (_, _) => await SwitchSelected(signInAgain: true);
        restore.Click += async (_, _) => await Run(() => Task.Run(() => service.Recover(true)), "Previous settings restored.");
        keep.Click += async (_, _) =>
        {
            if (Confirm("Keep the current launcher settings and discard the recovery record?"))
                await Run(() => Task.Run(() => service.Recover(false)), "Current settings kept.");
        };
        accounts.SelectedIndexChanged += (_, _) => SetControls();
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        capture.Tick += (_, _) => CaptureTick();
        expiry.Tick += (_, _) => StopWatching();
        Shown += (_, _) =>
        {
            if (loaded)
                return;
            RefreshView(announceRecovery: true);
            tray.Visible = true;
        };
        FormClosing += (_, args) =>
        {
            if (args.CloseReason != CloseReason.UserClosing || exiting)
                return;
            args.Cancel = true;
            Hide();
        };
    }

    private Account? Selected => accounts.SelectedItem as Account;

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18), ColumnCount = 1, RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Accounts", Font = headingFont, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty
        };
        layout.Controls.Add(SwitcherStyle.Split(title, add, new Padding(0, 0, 0, 15)), 0, 0);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = SwitcherStyle.Border, Margin = Padding.Empty, Padding = new Padding(1) };
        var cardContent = new Panel { Dock = DockStyle.Fill, BackColor = SwitcherStyle.Surface };
        accounts.BackColor = SwitcherStyle.Surface;
        emptyAccounts.Text = "No saved accounts. Select Add account to get started.";
        emptyAccounts.ForeColor = SwitcherStyle.SecondaryText;
        emptyAccounts.Padding = new Padding(14);
        cardContent.Controls.Add(accounts);
        cardContent.Controls.Add(emptyAccounts);
        card.Controls.Add(cardContent);
        layout.Controls.Add(card, 0, 1);

        var actions = SwitcherStyle.ButtonRow(switchAccount, signIn, remove, restore, keep);
        actions.Margin = new Padding(0, 12, 0, 12);
        layout.Controls.Add(actions, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = true, Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        status.Margin = new Padding(0, 0, 0, 8);
        status.Padding = new Padding(10, 7, 10, 7);
        status.BackColor = SwitcherStyle.Info;
        status.Visible = false;
        status.TextChanged += (_, _) =>
        {
            status.AccessibleName = status.Text;
            status.Visible = status.Text.Length > 0;
        };
        footer.SizeChanged += (_, _) => status.MaximumSize = new Size(footer.ClientSize.Width, 0);
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(new Panel
        {
            Height = 1, Dock = DockStyle.Fill, BackColor = SwitcherStyle.Border, Margin = new Padding(0, 0, 0, 8)
        }, 0, 1);
        path.ForeColor = SwitcherStyle.SecondaryText;
        path.Margin = new Padding(0, 0, 10, 0);
        footer.Controls.Add(SwitcherStyle.Split(path, choose, Padding.Empty), 0, 2);
        layout.Controls.Add(footer, 0, 3);
        return layout;
    }

    private void DrawAccount(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || accounts.Items[e.Index] is not Account account)
            return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(selected ? SwitcherStyle.SelectionBrush : SwitcherStyle.SurfaceBrush, e.Bounds);
        var inset = Pixels(14);
        var pillHeight = Pixels(26);
        var pill = new Rectangle(e.Bounds.Right - inset - Pixels(116),
            e.Bounds.Y + (e.Bounds.Height - pillHeight) / 2, Pixels(116), pillHeight);
        var name = new Rectangle(e.Bounds.X + inset, e.Bounds.Y,
            Math.Max(0, pill.Left - e.Bounds.X - (inset * 2)), e.Bounds.Height);
        const TextFormatFlags rowText = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, account.Label, accountFont, name, SwitcherStyle.RowText(selected), rowText);

        var saved = account.Session is not null;
        e.Graphics.FillRectangle(SwitcherStyle.StatusBrush(saved, selected), pill);
        TextRenderer.DrawText(e.Graphics, saved ? "Session saved" : "Sign-in required", bodyFont, pill,
            SwitcherStyle.StatusText(saved, selected), rowText | TextFormatFlags.HorizontalCenter);
        e.Graphics.DrawLine(SwitcherStyle.BorderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        if ((e.State & DrawItemState.Focus) != 0)
            e.DrawFocusRectangle();
    }

    private int Pixels(int value) => LogicalToDeviceUnits(value);

    private void RefreshView(Guid? select = null, bool announceRecovery = false)
    {
        var preferred = select ?? Selected?.Id;
        try
        {
            vault = announceRecovery ? service.ReadWithLauncherDetection(LauncherDiscovery.Find) : service.Read();
            loaded = true;
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            loaded = false;
            status.Text = "The local account vault could not be opened.";
        }

        accounts.DataSource = vault.Accounts;
        accounts.Visible = vault.Accounts.Length > 0;
        emptyAccounts.Visible = vault.Accounts.Length == 0 && loaded;
        for (var i = 0; i < accounts.Items.Count; i++)
        {
            if (((Account)accounts.Items[i]).Id == preferred)
            {
                accounts.SelectedIndex = i;
                break;
            }
        }
        path.Text = vault.LauncherPath is { } launcher ? $"Launcher: {launcher}" : "Launcher location not set";
        if (announceRecovery && vault.Pending is not null)
            status.Text = "A switch was interrupted. Restore the previous settings or keep the current settings.";
        SetControls();
        RebuildTray();
    }

    private void SetControls()
    {
        var recovery = vault.Pending is not null;
        var ready = loaded && !busy && !recovery;
        choose.Enabled = add.Enabled = ready;
        switchAccount.Enabled = signIn.Enabled = ready && vault.LauncherPath is not null && Selected is not null;
        remove.Enabled = ready && Selected is not null;
        switchAccount.Visible = signIn.Visible = remove.Visible = !recovery;
        restore.Visible = keep.Visible = recovery;
        restore.Enabled = keep.Enabled = loaded && !busy;
    }

    private void RebuildTray()
    {
        while (menu.Items.Count > 0)
            menu.Items[0].Dispose();
        menu.Items.Add("Open account switcher", null, (_, _) => ShowWindow());
        if (loaded && vault.Pending is null && vault.LauncherPath is not null)
        {
            foreach (var account in vault.Accounts)
            {
                var item = new ToolStripMenuItem(account.Label.Replace("&", "&&"))
                {
                    Enabled = !busy
                };
                item.Click += async (_, _) => await SwitchAccount(account.Id);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        var exit = menu.Items.Add("Exit", null, (_, _) =>
        {
            exiting = true;
            Close();
        });
        exit.Enabled = !busy;
    }

    private void ChooseLauncher()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "BSG Launcher (BsgLauncher.exe)|BsgLauncher.exe",
            FileName = "BsgLauncher.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || busy)
            return;
        StopWatching();
        Edit(() => service.SetPath(dialog.FileName), "Launcher location saved.");
    }

    private async Task AddAccount()
    {
        using var dialog = new AccountDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || busy)
            return;

        Account? account = null;
        Edit(() => account = service.Add(dialog.AccountName, dialog.Email), "Account added.");
        if (account is null)
            return;
        RefreshView(account.Id);
        if (vault.LauncherPath is not null)
            await SwitchAccount(account.Id);
    }

    private void RemoveAccount()
    {
        if (Selected is not { } account || !Confirm($"Remove the saved account “{account.Label}”?") || busy)
            return;

        if (watchedAccount == account.Id)
            StopWatching();
        Edit(() => service.Remove(account.Id), "Saved account removed.");
    }

    private Task SwitchSelected(bool signInAgain = false) =>
        Selected is { } account ? SwitchAccount(account.Id, signInAgain) : Task.CompletedTask;

    private async Task SwitchAccount(Guid id, bool signInAgain = false)
    {
        if (busy)
            return;
        StopWatching();
        await Run(async () =>
        {
            var saved = await Task.Run(() => service.SwitchAsync(id, signInAgain));
            status.Text = saved ? "Launcher started with the saved session." : "Launcher started. Sign in there to save a session.";
            StartWatching(id);
        });
    }

    private void StartWatching(Guid id)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        watchedAccount = id;
        try
        {
            watcher = new FileSystemWatcher(directory, Path.GetFileName(settingsPath))
            {
                SynchronizingObject = this,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => QueueCapture();
            watcher.Created += (_, _) => QueueCapture();
            watcher.Renamed += (_, _) => QueueCapture();
            watcher.Error += (_, _) => QueueCapture();
            watcher.EnableRaisingEvents = true;
            expiry.Start();
            QueueCapture();
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            StopWatching();
            status.Text += " Session monitoring is unavailable. Sessions can still be saved before the next switch.";
        }
    }

    private void QueueCapture()
    {
        if (watcher is null)
            return;
        captureAttempts = 0;
        capture.Stop();
        capture.Start();
    }

    private void CaptureTick()
    {
        if (busy)
            return;
        capture.Stop();
        try
        {
            if (service.CaptureLogin(watchedAccount))
            {
                status.Text = "Session saved.";
                RefreshView();
            }
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            if (++captureAttempts < 5)
                capture.Start();
        }
    }

    private void StopWatching()
    {
        expiry.Stop();
        capture.Stop();
        watcher?.Dispose();
        watcher = null;
        watchedAccount = Guid.Empty;
    }

    private void Edit(Action action, string success)
    {
        try
        {
            action();
            status.Text = success;
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            status.Text = Message(error);
        }
        RefreshView();
    }

    private async Task Run(Func<Task> operation, string? success = null)
    {
        if (busy)
            return;
        busy = true;
        status.Text = "Working…";
        SetControls();
        RebuildTray();
        try
        {
            await operation();
            if (success is not null)
                status.Text = success;
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            status.Text = Message(error);
            ShowWindow();
        }
        finally
        {
            busy = false;
            RefreshView();
        }
    }

    private void ShowWindow()
    {
        if (!loaded)
            RefreshView(announceRecovery: true);
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private bool Confirm(string message) => MessageBox.Show(this, message, AppTitle, MessageBoxButtons.YesNo,
        MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private static string Message(Exception error) => error switch
    {
        InvalidDataException or InvalidOperationException => error.Message,
        _ => "The operation could not finish. Check access to the launcher and its settings."
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            StopWatching();
            capture.Dispose();
            expiry.Dispose();
            tray.Dispose();
            menu.Dispose();
            headingFont.Dispose();
            accountFont.Dispose();
            bodyFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class AccountDialog : Form
    {
        private readonly TextBox label = new() { Dock = DockStyle.Fill, MaxLength = 64, AccessibleName = "Account name" };
        private readonly TextBox email = new() { Dock = DockStyle.Fill, MaxLength = 254, AccessibleName = "Account email" };

        internal string AccountName => label.Text.Trim();
        internal string Email => email.Text.Trim();

        internal AccountDialog()
        {
            Text = "Add a Tarkov account";
            ClientSize = new Size(430, 220);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = SwitcherStyle.Canvas;
            ForeColor = SwitcherStyle.Text;
            var save = SwitcherStyle.Button("Add account", primary: true);
            var cancel = SwitcherStyle.Button("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
            label.Margin = new Padding(0, 5, 0, 14);
            email.Margin = new Padding(0, 5, 0, 16);
            layout.Controls.Add(new Label { Text = "Account name", AutoSize = true, Margin = Padding.Empty });
            layout.Controls.Add(label);
            layout.Controls.Add(new Label { Text = "BSG account email", AutoSize = true, Margin = Padding.Empty });
            layout.Controls.Add(email);
            layout.Controls.Add(SwitcherStyle.ButtonRow(save, cancel));
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
            save.Click += (_, _) =>
            {
                if (AccountName.Length == 0 || !MailAddress.TryCreate(Email, out var address) || address.Address != Email)
                {
                    MessageBox.Show(this, "Enter a name and a valid BSG account email.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
        }
    }
}
