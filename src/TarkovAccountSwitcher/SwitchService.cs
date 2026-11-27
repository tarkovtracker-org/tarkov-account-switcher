using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace TarkovAccountSwitcher;

internal interface ILauncher
{
    void ValidatePath(string path);
    Task CloseAsync(string path);
    void EnsureStopped();
    bool IsRunning(string path);
    void Start(string path);
}

internal sealed class SwitchService(VaultStore store, LauncherSettings settings, ILauncher launcher)
{
    internal const int MaxNicknameLength = 64;

    internal Vault Read() => store.Load();

    internal Guid? ReadActiveAccount(Vault vault)
    {
        if (vault.Pending is not null || vault.LauncherPath is null || vault.Accounts.Length == 0)
            return null;

        try
        {
            return MatchActiveAccount(vault, LauncherSettings.Parse(settings.Read()));
        }
        catch (Exception error) when (IsExpected(error))
        {
            return null;
        }
    }

    internal Vault ReadWithLauncherDetection(Func<string?> discover)
    {
        var vault = Read();
        if (vault.LauncherPath is not null || vault.Pending is not null || discover() is not { } path)
            return vault;

        try
        {
            launcher.ValidatePath(path);
        }
        catch (InvalidOperationException)
        {
            return vault;
        }
        var configured = vault with { LauncherPath = path };
        store.Save(configured);
        return configured;
    }

    internal void SetPath(string path)
    {
        launcher.ValidatePath(path);
        store.Save(Ready() with { LauncherPath = Path.GetFullPath(path) });
    }

    internal Guid SaveCurrentAccount(string nickname)
    {
        nickname = nickname.Trim();
        if (nickname.Length is 0 or > MaxNicknameLength)
            throw new InvalidOperationException("Enter a nickname of 1 to 64 characters.");

        var vault = Ready();
        var path = vault.LauncherPath ?? throw new InvalidOperationException("Choose the BSG launcher first.");
        if (!launcher.IsRunning(path))
            throw new InvalidOperationException("Open the BSG Launcher and sign in before saving.");

        var (login, session) = LauncherSettings.ReadSession(LauncherSettings.Parse(settings.Read()));
        if (login is null || session is null)
            throw new InvalidOperationException("Finish signing in through the BSG Launcher, then try again.");
        if (!launcher.IsRunning(path))
            throw new InvalidOperationException("Leave the BSG Launcher open while saving the account.");

        var index = Array.FindIndex(vault.Accounts, account => SameEmail(account.Email, login));
        if (index < 0)
        {
            var account = new Account(Guid.NewGuid(), nickname, login, session);
            store.Save(vault with { Accounts = [.. vault.Accounts, account] });
            return account.Id;
        }

        var accounts = (Account[])vault.Accounts.Clone();
        var existing = accounts[index];
        accounts[index] = existing with { Label = nickname, Session = session };
        store.Save(vault with { Accounts = accounts });
        return existing.Id;
    }

    internal void Remove(Guid id)
    {
        var vault = Ready();
        store.Save(vault with { Accounts = vault.Accounts.Where(account => account.Id != id).ToArray() });
    }

    internal async Task<bool> SwitchAsync(Guid id, bool signInAgain = false)
    {
        var vault = Ready();
        var path = vault.LauncherPath ?? throw new InvalidOperationException("Choose BsgLauncher.exe first.");
        GetAccount(vault, id);
        launcher.ValidatePath(path);
        var current = LauncherSettings.Parse(settings.Read());
        if (!signInAgain && MatchActiveAccount(vault, current) == id)
            throw new InvalidOperationException("This account is already active in the BSG Launcher.");

        await launcher.CloseAsync(path).ConfigureAwait(false);
        launcher.EnsureStopped();
        var original = settings.Read();
        var document = LauncherSettings.Parse(original);
        vault = Capture(vault, document);
        var account = GetAccount(vault, id);
        var next = signInAgain ? account with { Session = null } : account;
        var replacement = LauncherSettings.Prepare(document, next);
        var recovery = new Recovery(Guid.NewGuid(), original, SHA256.HashData(replacement));
        var transaction = vault with { Pending = recovery };
        store.Save(transaction);

        try
        {
            launcher.EnsureStopped();
            if (!SameContents(settings.Read(), original))
                throw new InvalidOperationException("Launcher settings changed during the switch.");
            settings.Write(replacement, recovery.Id);
            launcher.Start(path);
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            try
            {
                Restore(transaction);
            }
            catch (Exception restoreFailure) when (IsExpected(restoreFailure))
            {
                throw new InvalidOperationException("The switch stopped. Review the pending settings recovery.");
            }
            throw;
        }

        if (signInAgain)
            vault = vault with { Accounts = vault.Accounts.Select(item => item.Id == id ? next : item).ToArray() };
        try
        {
            store.Save(vault);
        }
        catch (Exception error) when (IsExpected(error))
        {
            throw new InvalidOperationException("Launcher started. Review the pending settings recovery.");
        }
        return next.Session is not null;
    }

    internal bool CaptureLogin(Guid id)
    {
        var vault = Ready();
        return vault.LauncherPath is { } path && launcher.IsRunning(path) &&
            !ReferenceEquals(Capture(vault, LauncherSettings.Parse(settings.Read()), id), vault);
    }

    internal void Recover(bool restore)
    {
        var vault = store.Load();
        if (vault.Pending is null)
            return;
        if (restore)
            Restore(vault);
        else
        {
            settings.ClearStaging(vault.Pending.Id);
            store.Save(vault with { Pending = null });
        }
    }

    internal static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or CryptographicException or JsonException or ArgumentException
            or InvalidDataException or TimeoutException;

    private Vault Ready()
    {
        var vault = store.Load();
        if (vault.Pending is not null)
            throw new InvalidOperationException("Resolve the pending settings recovery first.");
        return vault;
    }

    private void Restore(Vault vault)
    {
        var recovery = vault.Pending!;
        var current = settings.Read();
        settings.ClearStaging(recovery.Id);
        if (!SameContents(current, recovery.Original))
        {
            if (current is null || !SHA256.HashData(current).AsSpan().SequenceEqual(recovery.AppliedHash))
                throw new InvalidOperationException("The launcher settings changed after the switch. Keep the current settings to continue.");
            launcher.EnsureStopped();
            if (!SameContents(settings.Read(), current))
                throw new InvalidOperationException("Launcher settings changed during recovery.");
            settings.Restore(recovery.Original, recovery.Id);
        }
        store.Save(vault with { Pending = null });
    }

    private Vault Capture(Vault vault, System.Text.Json.Nodes.JsonObject document, Guid? expected = null)
    {
        var (login, session) = LauncherSettings.ReadSession(document);
        if (login is null || session is null)
            return vault;

        var index = Array.FindIndex(vault.Accounts, account =>
            (expected is null || account.Id == expected) && SameEmail(account.Email, login));
        if (index < 0 || vault.Accounts[index].Session == session)
            return vault;

        var accounts = (Account[])vault.Accounts.Clone();
        accounts[index] = accounts[index] with { Session = session };
        vault = vault with { Accounts = accounts };
        store.Save(vault);
        return vault;
    }

    private static Account GetAccount(Vault vault, Guid id) =>
        vault.Accounts.FirstOrDefault(account => account.Id == id)
        ?? throw new InvalidOperationException("The selected account is unavailable.");

    private Guid? MatchActiveAccount(Vault vault, System.Text.Json.Nodes.JsonObject document)
    {
        var (login, session) = LauncherSettings.ReadSession(document);
        if (login is null || session is null || vault.LauncherPath is not { } path)
            return null;

        Guid? match = null;
        foreach (var account in vault.Accounts)
        {
            if (!SameEmail(account.Email, login))
                continue;
            if (match is not null)
                return null;
            match = account.Id;
        }

        return match is not null && launcher.IsRunning(path) ? match : null;
    }

    private static bool SameEmail(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameContents(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
}
