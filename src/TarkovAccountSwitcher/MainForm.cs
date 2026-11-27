using System.Net.Mail;

namespace TarkovAccountSwitcher;

internal sealed class MainForm : Form
{
    internal const string AppTitle = "Tarkov Account Switcher";
    private readonly SwitchService service;
    private readonly string settingsPath;
    private readonly ListBox accounts = new() { Dock = DockStyle.Fill, AccessibleName = "Saved accounts" };
    private readonly Label path = new() { AutoSize = true, Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "Switch status" };
    private readonly Button choose = new() { Text = "Choose launcher…", AutoSize = true };
    private readonly Button switchAccount = new() { Text = "Switch account", AutoSize = true };
    private readonly Button add = new() { Text = "Add account…", AutoSize = true };
    private readonly Button signIn = new() { Text = "Sign in again", AutoSize = true };
    private readonly Button remove = new() { Text = "Remove…", AutoSize = true };
    private readonly Button restore = new() { Text = "Restore previous settings", AutoSize = true };
    private readonly Button keep = new() { Text = "Keep current settings…", AutoSize = true };
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
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(570, 360);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(path, 0, 0);
        layout.Controls.Add(ButtonRow(choose), 0, 1);
        layout.Controls.Add(accounts, 0, 2);
        layout.Controls.Add(ButtonRow(switchAccount, add, signIn, remove, restore, keep), 0, 3);
        layout.Controls.Add(status, 0, 4);
        Controls.Add(layout);

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

    private void RefreshView(Guid? select = null, bool announceRecovery = false)
    {
        var preferred = select ?? Selected?.Id;
        try
        {
            vault = service.Read();
            loaded = true;
        }
        catch (Exception error) when (SwitchService.IsExpected(error))
        {
            loaded = false;
            status.Text = "The local account vault could not be opened.";
        }

        accounts.DataSource = vault.Accounts;
        for (var i = 0; i < accounts.Items.Count; i++)
        {
            if (((Account)accounts.Items[i]).Id == preferred)
            {
                accounts.SelectedIndex = i;
                break;
            }
        }
        path.Text = vault.LauncherPath is { } launcher ? $"Launcher: {launcher}" : "Choose the standalone BSG launcher to begin.";
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

    private static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        row.Controls.AddRange(buttons);
        return row;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            StopWatching();
            capture.Dispose();
            expiry.Dispose();
            tray.Dispose();
            menu.Dispose();
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
            ClientSize = new Size(370, 160);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            var save = new Button { Text = "Add account", AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
            layout.Controls.Add(new Label { Text = "Account name", AutoSize = true });
            layout.Controls.Add(label);
            layout.Controls.Add(new Label { Text = "BSG account email", AutoSize = true });
            layout.Controls.Add(email);
            layout.Controls.Add(ButtonRow(save, cancel));
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
