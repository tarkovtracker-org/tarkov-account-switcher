namespace TarkovAccountSwitcher;

internal sealed class MainForm : SwitcherForm
{
    internal const string AppTitle = "Tarkov Account Switcher";
    private readonly SwitchService service;
    private readonly string settingsPath;
    private readonly AppUpdates? updates;
    private readonly CancellationTokenSource updateLifetime = new();
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
    private readonly SwitcherButton choose = SwitcherStyle.Button("Choose launcher…");
    private readonly SwitcherButton switchAccount = SwitcherStyle.Button("Switch account", primary: true);
    private readonly SwitcherButton saveCurrent = SwitcherStyle.Button("Save current BSG account…");
    private readonly SwitcherButton signIn = SwitcherStyle.Button("Sign in again");
    private readonly SwitcherButton remove = SwitcherStyle.Button("Remove…");
    private readonly SwitcherButton restore = SwitcherStyle.Button("Restore previous settings", primary: true);
    private readonly SwitcherButton keep = SwitcherStyle.Button("Keep current settings…");
    private readonly SwitcherButton updateButton = SwitcherStyle.Button("Check for updates");
    private readonly Label updateStatus = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 22, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 10, 0)
    };
    private readonly Panel card = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1) };
    private readonly Panel cardContent = new() { Dock = DockStyle.Fill };
    private readonly Panel footerRule = new() { Height = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
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
    private bool checkingUpdate;
    private UpdateRelease? availableUpdate;

    internal MainForm(SwitchService service, string settingsPath, Func<SwitcherPalette>? readPalette = null,
        AppUpdates? updates = null)
        : base(readPalette)
    {
        this.service = service;
        this.settingsPath = settingsPath;
        this.updates = updates;
        Text = AppTitle;
        Icon = SystemIcons.Application;
        ClientSize = new Size(680, updates is null ? 465 : 510);
        MinimumSize = new Size(600, updates is null ? 415 : 460);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = bodyFont;
        Controls.Add(BuildLayout());
        ApplyPalette();

        accounts.DrawItem += DrawAccount;
        SwitcherStyle.SetAccountRowHeight(accounts, Pixels(54));
        DpiChanged += (_, _) => SwitcherStyle.SetAccountRowHeight(accounts, Pixels(54));
        AcceptButton = switchAccount;

        choose.Click += (_, _) => ChooseLauncher();
        updateButton.Click += async (_, _) => await UpdateAction();
        saveCurrent.Click += (_, _) => SaveCurrentAccount();
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
        Shown += async (_, _) =>
        {
            if (loaded)
                return;
            RefreshView(announceRecovery: true);
            tray.Visible = true;
            await CheckForUpdates(manual: false);
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
    internal bool HasLoadedVault => loaded;
    internal void ReportUpdateIssue(string message) => updateStatus.Text = message;

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
        layout.Controls.Add(SwitcherStyle.Split(title, saveCurrent, new Padding(0, 0, 0, 15)), 0, 0);

        emptyAccounts.Text = "Sign in through the BSG Launcher, then save your current account.";
        emptyAccounts.Padding = new Padding(14);
        cardContent.Controls.Add(accounts);
        cardContent.Controls.Add(emptyAccounts);
        card.Controls.Add(cardContent);
        layout.Controls.Add(card, 0, 1);

        var actions = SwitcherStyle.ButtonRow(switchAccount, signIn, remove, restore, keep);
        actions.Margin = new Padding(0, 12, 0, 12);
        layout.Controls.Add(actions, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, AutoSize = true, Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        status.Margin = new Padding(0, 0, 0, 8);
        status.Padding = new Padding(10, 7, 10, 7);
        status.Visible = false;
        status.TextChanged += (_, _) =>
        {
            status.AccessibleName = status.Text;
            status.Visible = status.Text.Length > 0;
        };
        footer.SizeChanged += (_, _) => status.MaximumSize = new Size(footer.ClientSize.Width, 0);
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(footerRule, 0, 1);
        path.Margin = new Padding(0, 0, 10, 0);
        footer.Controls.Add(SwitcherStyle.Split(path, choose, Padding.Empty), 0, 2);
        if (updates is not null)
        {
            updateStatus.Text = $"Version {updates.Current}";
            footer.Controls.Add(SwitcherStyle.Split(updateStatus, updateButton, new Padding(0, 8, 0, 0)), 0, 3);
        }
        layout.Controls.Add(footer, 0, 3);
        return layout;
    }

    protected override void ApplyPalette()
    {
        base.ApplyPalette();
        card.BackColor = footerRule.BackColor = Palette.Border;
        cardContent.BackColor = accounts.BackColor = Palette.Surface;
        accounts.ForeColor = Palette.Text;
        emptyAccounts.ForeColor = path.ForeColor = updateStatus.ForeColor = Palette.SecondaryText;
        status.BackColor = Palette.Info;
        foreach (var button in new[] { choose, switchAccount, saveCurrent, signIn, remove, restore, keep, updateButton })
            button.ApplyPalette(Palette);
        SwitcherStyle.StyleMenu(menu, Palette);
    }

    private void DrawAccount(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || accounts.Items[e.Index] is not Account account)
            return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(selected ? Palette.Selection : Palette.Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var inset = Pixels(14);
        var pillHeight = Pixels(26);
        var pill = new Rectangle(e.Bounds.Right - inset - Pixels(116),
            e.Bounds.Y + (e.Bounds.Height - pillHeight) / 2, Pixels(116), pillHeight);
        var name = new Rectangle(e.Bounds.X + inset, e.Bounds.Y,
            Math.Max(0, pill.Left - e.Bounds.X - (inset * 2)), e.Bounds.Height);
        const TextFormatFlags rowText = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var foreground = selected ? Palette.SelectionText : Palette.Text;
        TextRenderer.DrawText(e.Graphics, account.Label, accountFont, name, foreground, rowText);

        var saved = account.Session is not null;
        var (back, fore) = Palette.SessionColors(saved, selected);
        brush.Color = back;
        e.Graphics.FillRectangle(brush, pill);
        TextRenderer.DrawText(e.Graphics, saved ? "Session saved" : "Sign-in required", bodyFont, pill,
            fore, rowText | TextFormatFlags.HorizontalCenter);
        using var rule = new Pen(Palette.Border);
        e.Graphics.DrawLine(rule, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
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
        choose.Enabled = ready;
        saveCurrent.Enabled = ready && vault.LauncherPath is not null;
        switchAccount.Enabled = signIn.Enabled = saveCurrent.Enabled && Selected is not null;
        remove.Enabled = ready && Selected is not null;
        switchAccount.Visible = signIn.Visible = remove.Visible = !recovery;
        restore.Visible = keep.Visible = recovery;
        restore.Enabled = keep.Enabled = loaded && !busy;
        updateButton.Enabled = loaded && !busy && !checkingUpdate;
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
        if (updates is not null)
        {
            var update = menu.Items.Add(updateButton.Text, null, async (_, _) =>
            {
                ShowWindow();
                await UpdateAction();
            });
            update.Enabled = updateButton.Enabled;
        }
        var exit = menu.Items.Add("Exit", null, (_, _) =>
        {
            exiting = true;
            Close();
        });
        exit.Enabled = !busy;
    }

    private Task UpdateAction() => availableUpdate is { } release
        ? InstallUpdate(release)
        : CheckForUpdates(manual: true);

    private async Task CheckForUpdates(bool manual)
    {
        if (updates is null || busy || checkingUpdate)
            return;

        checkingUpdate = true;
        SetControls();
        RebuildTray();
        if (manual)
            updateStatus.Text = "Checking GitHub releases…";

        try
        {
            availableUpdate = await updates.Client.CheckAsync(updates.Current, updateLifetime.Token);
            if (IsDisposed)
                return;

            if (availableUpdate is { } available)
            {
                updateButton.Text = $"Update to {available.Tag}";
                updateStatus.Text = $"{available.Tag} is available.";
            }
            else if (manual)
            {
                updateStatus.Text = $"Version {updates.Current} is up to date.";
            }
        }
        catch (Exception error) when (UpdateInstallations.IsExpected(error))
        {
            if (manual && !IsDisposed)
                updateStatus.Text = "Could not check for a verified GitHub release. Try again.";
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed)
            {
                SetControls();
                RebuildTray();
            }
        }
    }

    private async Task InstallUpdate(UpdateRelease release)
    {
        if (updates is null || busy || checkingUpdate || !loaded)
            return;

        busy = true;
        SetControls();
        RebuildTray();
        updateStatus.Text = $"Downloading and verifying {release.Tag}…";
        try
        {
            var cancellation = updateLifetime.Token;
            var install = await Task.Run(() => updates.Installations.StageAsync(updates.Client, release, cancellation));
            if (IsDisposed)
                return;
            updates.Installations.StartPending(install);
        }
        catch (Exception error) when (UpdateInstallations.IsExpected(error))
        {
            if (!IsDisposed)
            {
                updateStatus.Text = error is InvalidDataException
                    ? "The update could not be verified."
                    : "The update could not start. Try again.";
                busy = false;
                SetControls();
                RebuildTray();
            }
            return;
        }

        exiting = true;
        Close();
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

    private void SaveCurrentAccount()
    {
        using var dialog = new SaveAccountDialog(ReadPalette, nickname =>
        {
            if (busy)
                throw new InvalidOperationException("Wait for the current operation to finish, then try again.");
            return service.SaveCurrentAccount(nickname);
        });
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SavedAccountId is { } id)
        {
            status.Text = "Account saved.";
            RefreshView(id);
        }
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
            updateLifetime.Cancel();
            updateLifetime.Dispose();
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

    private sealed class SaveAccountDialog : SwitcherForm
    {
        private readonly TextBox nickname = new()
        {
            Dock = DockStyle.Fill, MaxLength = SwitchService.MaxNicknameLength, AccessibleName = "Account nickname"
        };
        private readonly Label error = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 10, 0, 0) };
        private readonly SwitcherButton save = SwitcherStyle.Button("Save account", primary: true);
        private readonly SwitcherButton cancel = SwitcherStyle.Button("Cancel");

        internal Guid? SavedAccountId { get; private set; }

        internal SaveAccountDialog(Func<SwitcherPalette> readPalette, Func<string, Guid> saveAccount) : base(readPalette)
        {
            Text = "Save current BSG account";
            ClientSize = new Size(470, 240);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            cancel.DialogResult = DialogResult.Cancel;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            for (var row = 0; row < 5; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var description = new Label
            {
                Text = "Sign in directly in the BSG Launcher and leave it open. This saves your current launcher session on this PC so you can switch back later.",
                AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(0, 0, 0, 16)
            };
            error.MaximumSize = description.MaximumSize;
            nickname.Margin = new Padding(0, 5, 0, 16);
            layout.Controls.Add(description);
            layout.Controls.Add(new Label { Text = "Nickname", AutoSize = true, Margin = Padding.Empty });
            layout.Controls.Add(nickname);
            layout.Controls.Add(SwitcherStyle.ButtonRow(save, cancel));
            layout.Controls.Add(error);
            Controls.Add(layout);
            ApplyPalette();
            AcceptButton = save;
            CancelButton = cancel;
            ActiveControl = nickname;
            save.Click += (_, _) =>
            {
                try
                {
                    SavedAccountId = saveAccount(nickname.Text);
                    DialogResult = DialogResult.OK;
                }
                catch (Exception failure) when (SwitchService.IsExpected(failure))
                {
                    error.Text = Message(failure);
                    error.Visible = true;
                }
            };
        }

        protected override void ApplyPalette()
        {
            base.ApplyPalette();
            nickname.BackColor = Palette.Surface;
            nickname.ForeColor = Palette.Text;
            save.ApplyPalette(Palette);
            cancel.ApplyPalette(Palette);
        }
    }
}
