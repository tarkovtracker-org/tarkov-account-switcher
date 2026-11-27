using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovAccountSwitcher;

internal sealed record SavedSession(string Access, string Refresh, JsonElement? Expiration)
{
    public bool Equals(SavedSession? other) =>
        other is not null && Access == other.Access && Refresh == other.Refresh &&
        Expiration?.GetRawText() == other.Expiration?.GetRawText();

    public override int GetHashCode() => HashCode.Combine(Access, Refresh, Expiration?.GetRawText());
}

internal sealed class LauncherSettings(string path)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    internal string Path { get; } = path;
    internal byte[]? Read() => AtomicFile.Read(Path);
    internal void Write(byte[] bytes, Guid transaction) => AtomicFile.Write(Path, bytes, transaction);
    internal void ClearStaging(Guid transaction)
    {
        if (Directory.Exists(System.IO.Path.GetDirectoryName(Path)))
            File.Delete(AtomicFile.StagingPath(Path, transaction));
    }

    internal void Restore(byte[]? original, Guid transaction)
    {
        if (original is null)
            File.Delete(Path);
        else
            Write(original, transaction);
    }

    internal static JsonObject Parse(byte[]? bytes)
    {
        if (bytes is null)
            return new JsonObject();

        try
        {
            var json = bytes.AsSpan();
            if (json.StartsWith(Encoding.UTF8.Preamble))
                json = json[Encoding.UTF8.Preamble.Length..];
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The launcher settings format is unsupported.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The launcher settings could not be read. Open the launcher to check its settings.");
        }
    }

    internal static (string? Email, SavedSession? Session) ReadSession(JsonObject settings)
    {
        var email = Text(settings, "login");
        var access = Text(settings, "at");
        var refresh = Text(settings, "rt");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(access) ||
            string.IsNullOrWhiteSpace(refresh))
            return (email, null);

        var expiration = settings["atet"] is { } value ? JsonSerializer.SerializeToElement(value) : (JsonElement?)null;
        return (email, new SavedSession(access, refresh, expiration));
    }

    internal static byte[] Prepare(JsonObject settings, Account account)
    {
        settings["login"] = account.Email;
        settings["saveLogin"] = true;
        settings["keepLoggedIn"] = true;
        settings.Remove("at");
        settings.Remove("rt");
        settings.Remove("atet");
        if (account.Session is { } session)
        {
            settings["at"] = session.Access;
            settings["rt"] = session.Refresh;
            if (session.Expiration is { } expiration)
                settings["atet"] = JsonNode.Parse(expiration.GetRawText());
        }
        return Encoding.UTF8.GetBytes(settings.ToJsonString(Pretty));
    }

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
