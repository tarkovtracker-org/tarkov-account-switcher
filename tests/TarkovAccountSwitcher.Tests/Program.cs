using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TarkovAccountSwitcher;

var tests = new (string Name, Func<Fixture, Task> Run)[]
{
    ("Detects an unconfigured launcher once and preserves an explicit path", f =>
    {
        f.Store.Save(f.Store.Load() with { LauncherPath = null });
        Check(f.Service.ReadWithLauncherDetection(() => null).LauncherPath is null, "guessed a missing launcher");
        var detected = Path.Combine(Path.GetDirectoryName(f.Settings.Path)!, "BsgLauncher.exe");
        Check(f.Service.ReadWithLauncherDetection(() => detected).LauncherPath == detected, "launcher was not detected");
        Check(f.Service.Read().LauncherPath == detected, "detected path was not saved");
        var explicitPath = Path.Combine(Path.GetDirectoryName(f.Settings.Path)!, "custom", "BsgLauncher.exe");
        f.Service.SetPath(explicitPath);
        Check(f.Service.ReadWithLauncherDetection(() => throw new Exception("discovery reran")).LauncherPath == explicitPath,
            "changed the explicit path");
        return Task.CompletedTask;
    }),
    ("Detection leaves pending settings recovery untouched", f =>
    {
        f.Store.Save(f.Store.Load() with { LauncherPath = null, Pending = new Recovery(Guid.NewGuid(), null, []) });
        var result = f.Service.ReadWithLauncherDetection(() => throw new Exception("discovery ran during recovery"));
        Check(result.LauncherPath is null && result.Pending is not null, "altered pending recovery");
        return Task.CompletedTask;
    }),
    ("Manual selection remains available when the detected launcher disappears", f =>
    {
        f.Store.Save(f.Store.Load() with { LauncherPath = null });
        f.Process.OnValidate = () => throw new InvalidOperationException("Installation no longer exists");
        Check(f.Service.ReadWithLauncherDetection(() => "vanished").LauncherPath is null,
            "accepted a stale installation");
        f.Process.OnValidate = null;
        f.Service.SetPath(Path.Combine(Path.GetDirectoryName(f.Settings.Path)!, "BsgLauncher.exe"));
        Check(f.Service.Read().LauncherPath is not null, "manual selection did not save");
        return Task.CompletedTask;
    }),
    ("Closes before capturing, restores the requested session, preserves extra settings", async f =>
    {
        f.Write("one@example.test", "obsolete");
        await f.Service.SwitchAsync(f.One.Id);
        f.Write("two@example.test", "two");
        f.Process.OnClose = () => f.Write("two@example.test", "refreshed");
        Check(await f.Service.SwitchAsync(f.One.Id), "expected restored session");
        var current = LauncherSettings.Parse(f.Settings.Read());
        Check((string?)current["at"] == "obsolete", "wrong session was restored");
        Check((string?)current["arbitrary"]?["keep"] == "true", "unrelated settings were lost");
        Check(f.Service.Read().Accounts[1].Session?.Access == "refreshed", "did not capture after closing");
    }),
    ("Clears old auth fields for a fresh sign-in", async f =>
    {
        f.Write(f.One.Email, "one");
        Check(!await f.Service.SwitchAsync(f.Two.Id), "reported a saved session for a fresh account");
        var current = LauncherSettings.Parse(f.Settings.Read());
        Check((string?)current["login"] == f.Two.Email, "wrong email");
        Check(!current.ContainsKey("at") && !current.ContainsKey("rt") && !current.ContainsKey("atet"),
            "old authentication fields survived");
    }),
    ("Refused shutdown leaves settings unchanged and starts nothing", async f =>
    {
        f.Write(f.One.Email, "unchanged");
        var before = f.Settings.Read();
        f.Process.OnClose = () => throw new InvalidOperationException("Still running");
        await Reject(() => f.Service.SwitchAsync(f.Two.Id));
        Check(f.Process.Starts == 0 && before!.SequenceEqual(f.Settings.Read()!), "switched despite refused shutdown");
    }),
    ("Race with a launcher restart before write leaves settings unchanged", async f =>
    {
        f.Write(f.One.Email, "unchanged");
        var before = f.Settings.Read();
        f.Process.OnEnsureStopped = count =>
        {
            if (count == 2)
                throw new InvalidOperationException("Unexpected launcher");
        };
        await Reject(() => f.Service.SwitchAsync(f.Two.Id));
        Check(f.Process.Starts == 0 && before!.SequenceEqual(f.Settings.Read()!), "switched after restart");
        Check(f.Service.Read().Pending is null, "left a transaction before any change");
    }),
    ("Failed launch restores exact bytes and clears the journal", async f =>
    {
        f.Write(f.One.Email, "untouched");
        var before = f.Settings.Read();
        f.Process.OnStart = () => throw new InvalidOperationException("Start refused");
        await Reject(() => f.Service.SwitchAsync(f.Two.Id));
        Check(before!.SequenceEqual(f.Settings.Read()!), "rollback changed original bytes");
        Check(f.Store.Load().Pending is null, "left a completed rollback pending");
    }),
    ("Failed first launch restores a missing settings file", async f =>
    {
        f.Process.OnStart = () => throw new InvalidOperationException("Start refused");
        await Reject(() => f.Service.SwitchAsync(f.Two.Id));
        Check(f.Settings.Read() is null, "left settings that did not exist before the attempt");
        Check(f.Store.Load().Pending is null, "left a completed rollback pending");
    }),
    ("Reopening the vault recovers an interrupted switch", f =>
    {
        f.Write(f.One.Email, "before");
        var before = f.Settings.Read()!;
        f.Write(f.Two.Email, "applied");
        f.Journal(before);
        var staging = AtomicFile.StagingPath(f.Settings.Path, f.Store.Load().Pending!.Id);
        File.WriteAllText(staging, "interrupted staging file");
        var reopened = new SwitchService(new VaultStore(f.VaultPath), f.Settings, f.Process);
        reopened.Recover(true);
        Check(before.SequenceEqual(f.Settings.Read()!) && reopened.Read().Pending is null, "recovery failed");
        Check(!File.Exists(staging), "recovery left a staging file");
        return Task.CompletedTask;
    }),
    ("Recovery preserves intervening launcher writes", async f =>
    {
        f.Write(f.One.Email, "before");
        var before = f.Settings.Read();
        f.Write(f.Two.Email, "applied");
        f.Journal(before);
        f.Write(f.Two.Email, "external");
        var external = f.Settings.Read()!;
        await Reject(() => { f.Service.Recover(true); return Task.CompletedTask; });
        Check(external.SequenceEqual(f.Settings.Read()!) && f.Store.Load().Pending is not null,
            "recovery overwrote an external write");
        f.Service.Recover(false);
        Check(external.SequenceEqual(f.Settings.Read()!) && f.Store.Load().Pending is null,
            "keeping current settings altered them");
    }),
    ("Capture accepts only the requested account from the configured launcher", f =>
    {
        f.Write(f.Two.Email, "two");
        f.Service.CaptureLogin(f.One.Id);
        Check(f.Store.Load().Accounts.All(a => a.Session is null), "captured the wrong account");
        f.Process.Running = false;
        f.Service.CaptureLogin(f.Two.Id);
        Check(f.Store.Load().Accounts.All(a => a.Session is null), "captured without a running launcher");
        f.Process.Running = true;
        f.Service.CaptureLogin(f.Two.Id);
        Check(f.Store.Load().Accounts[1].Session?.Access == "two", "did not capture the chosen account");
        return Task.CompletedTask;
    }),
    ("Sign in again clears a saved session", async f =>
    {
        f.Write(f.One.Email, "old");
        Check(!await f.Service.SwitchAsync(f.One.Id, signInAgain: true), "reported a restored session");
        Check(f.Store.Load().Accounts[0].Session is null, "old session remained in the vault");
        Check(!LauncherSettings.Parse(f.Settings.Read()).ContainsKey("at"), "old session remained in the launcher");
    }),
    ("Malformed settings are rejected before closing the launcher", async f =>
    {
        File.WriteAllText(f.Settings.Path, "{broken");
        await Reject(() => f.Service.SwitchAsync(f.One.Id));
        Check(f.Process.Closes == 0 && f.Process.Starts == 0 && File.ReadAllText(f.Settings.Path) == "{broken",
            "malformed settings were used");
    }),
    ("Session and recovery data are authenticated and encrypted on disk", async f =>
    {
        const string marker = "synthetic-secret-for-storage-verification";
        f.Write(f.One.Email, marker);
        f.Service.CaptureLogin(f.One.Id);
        f.Journal(f.Settings.Read());
        var bytes = File.ReadAllBytes(f.VaultPath);
        Check(Encoding.UTF8.GetString(bytes).IndexOf(marker, StringComparison.Ordinal) < 0, "found plaintext");
        var reloaded = new VaultStore(f.VaultPath).Load();
        Check(reloaded.Accounts[0].Session?.Access == marker && reloaded.Pending?.Original is not null, "could not decrypt data");
        bytes[^1] ^= 1;
        File.WriteAllBytes(f.VaultPath, bytes);
        await Reject(() => { f.Store.Load(); return Task.CompletedTask; });
    })
};

var selected = args.Length == 0 ? tests : tests.Where(test => test.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
if (selected.Length == 0)
    throw new InvalidOperationException("No tests selected.");

var failed = 0;
foreach (var (name, run) in selected)
{
    using var fixture = new Fixture();
    try
    {
        await run(fixture);
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
return failed == 0 ? 0 : 1;

static void Check(bool condition, string description)
{
    if (!condition)
        throw new Exception(description);
}

static async Task Reject(Func<Task> operation)
{
    try
    {
        await operation();
    }
    catch (Exception error) when (SwitchService.IsExpected(error))
    {
        return;
    }
    throw new Exception("Expected the operation to reject.");
}

internal sealed class Fixture : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("tarkov-switch-tests-").FullName;

    internal string VaultPath => Path.Combine(directory, "vault.dat");
    internal VaultStore Store { get; }
    internal LauncherSettings Settings { get; }
    internal FakeLauncher Process { get; } = new();
    internal SwitchService Service { get; }
    internal Account One { get; }
    internal Account Two { get; }

    internal Fixture()
    {
        Store = new VaultStore(VaultPath);
        Settings = new LauncherSettings(Path.Combine(directory, "settings"));
        Service = new SwitchService(Store, Settings, Process);
        Service.SetPath(Path.Combine(directory, "BsgLauncher.exe"));
        One = Service.Add("One", "one@example.test");
        Two = Service.Add("Two", "two@example.test");
    }

    internal void Write(string email, string token)
    {
        var settings = new JsonObject
        {
            ["arbitrary"] = new JsonObject { ["keep"] = "true" },
            ["login"] = email,
            ["at"] = token,
            ["rt"] = token + "-refresh",
            ["atet"] = 12345
        };
        File.WriteAllText(Settings.Path, settings.ToJsonString(), new UTF8Encoding(true));
    }

    internal void Journal(byte[]? original) =>
        Store.Save(Store.Load() with { Pending = new Recovery(Guid.NewGuid(), original, SHA256.HashData(Settings.Read()!)) });

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

internal sealed class FakeLauncher : ILauncher
{
    internal Action? OnValidate { get; set; }
    internal Action? OnClose { get; set; }
    internal Action? OnStart { get; set; }
    internal Action<int>? OnEnsureStopped { get; set; }
    internal int Starts { get; private set; }
    internal int Closes { get; private set; }
    internal bool Running { get; set; } = true;
    private int stoppedChecks;

    public void ValidatePath(string path) => OnValidate?.Invoke();
    public bool IsRunning(string path) => Running;
    public void EnsureStopped() => OnEnsureStopped?.Invoke(++stoppedChecks);

    public Task CloseAsync(string path)
    {
        Closes++;
        OnClose?.Invoke();
        Running = false;
        return Task.CompletedTask;
    }

    public void Start(string path)
    {
        Starts++;
        OnStart?.Invoke();
        Running = true;
    }
}
