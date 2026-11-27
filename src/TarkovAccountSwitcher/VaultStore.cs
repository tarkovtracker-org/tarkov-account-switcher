using System.Security.Cryptography;
using System.Text.Json;

namespace TarkovAccountSwitcher;

internal sealed record Account(Guid Id, string Label, string Email, SavedSession? Session = null)
{
    public override string ToString() => $"{Label}  ·  {(Session is null ? "Sign-in required" : "Session saved")}";
}

internal sealed record Recovery(Guid Id, byte[]? Original, byte[] AppliedHash);

internal sealed record Vault(string? LauncherPath, Account[] Accounts, Recovery? Pending = null)
{
    internal static Vault Empty { get; } = new(null, []);
}

internal sealed class VaultStore(string path)
{
    internal Vault Load()
    {
        var protectedBytes = AtomicFile.Read(path);
        if (protectedBytes is null)
            return Vault.Empty;

        var plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Vault>(plaintext)
                ?? throw new InvalidDataException("The local vault is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Save(Vault vault)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(vault);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            AtomicFile.Write(path, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

internal static class AtomicFile
{
    internal static byte[]? Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static void Write(string path, byte[] contents, Guid? transaction = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = StagingPath(path, transaction ?? Guid.NewGuid());
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }

    internal static string StagingPath(string path, Guid transaction) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".tarkov-switch-{transaction:N}.tmp");
}
