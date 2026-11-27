using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TarkovAccountSwitcher;

internal sealed class WindowsLauncher : ILauncher
{
    private const string Executable = "BsgLauncher.exe";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(8);
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;

    public void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), Executable, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidOperationException("Choose an installed BsgLauncher.exe.");
    }

    public async Task CloseAsync(string path)
    {
        EnsureGameStopped();
        var processes = Find();
        try
        {
            VerifyInstallation(processes, path);
            if (processes.Length == 0)
                return;

            foreach (var process in processes)
                process.CloseMainWindow();

            if (await WaitForExit(processes, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                return;

            // Normal window close may only minimize a tray application.
            var remaining = processes.Where(process => !process.HasExited).Select(Identify).ToArray();
            if (remaining.Length > 0)
                RequestRestartManagerShutdown(remaining);

            if (!await WaitForExit(processes, ExitTimeout).ConfigureAwait(false))
                throw new InvalidOperationException("The launcher is still running. Exit it from its tray menu and try again.");
        }
        finally
        {
            Dispose(processes);
        }
    }

    public void EnsureStopped()
    {
        EnsureGameStopped();
        var processes = Find();
        try
        {
            if (processes.Any(process => !process.HasExited))
                throw new InvalidOperationException("Exit every BSG launcher before modifying launcher settings.");
        }
        finally
        {
            Dispose(processes);
        }
    }

    public bool IsRunning(string path)
    {
        var processes = Find();
        try
        {
            VerifyInstallation(processes, path);
            return processes.Any(process => !process.HasExited);
        }
        finally
        {
            Dispose(processes);
        }
    }

    public void Start(string path)
    {
        ValidatePath(path);
        using var started = Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Windows did not start the launcher.");
    }

    private static Process[] Find() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable));

    private void EnsureGameStopped()
    {
        foreach (var name in new[] { "EscapeFromTarkov", "EscapeFromTarkovArena" })
        {
            var games = Process.GetProcessesByName(name);
            try
            {
                if (games.Any(game => !game.HasExited && game.SessionId == sessionId))
                    throw new InvalidOperationException("Exit Tarkov and Arena before switching accounts.");
            }
            finally
            {
                Dispose(games);
            }
        }
    }

    private void VerifyInstallation(Process[] processes, string path)
    {
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                    continue;
                if (process.SessionId != sessionId ||
                    !string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different BSG launcher is running. Close it and try again.");
            }
            catch (Win32Exception) when (!process.HasExited)
            {
                throw new InvalidOperationException("The running launcher could not be verified. Close it and try again.");
            }
        }
    }

    private static async Task<bool> WaitForExit(Process[] processes, TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource(timeout);
        try
        {
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(stop.Token))).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void Dispose(Process[] processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }

    private static RestartProcess Identify(Process process)
    {
        var started = process.StartTime.ToFileTimeUtc();
        return new RestartProcess(process.Id, new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = unchecked((int)started),
            dwHighDateTime = (int)(started >> 32)
        });
    }

    private static void RequestRestartManagerShutdown(RestartProcess[] targets)
    {
        if (RmStartSession(out var handle, 0, new StringBuilder(33)) != 0)
            return;

        try
        {
            if (RmRegisterResources(handle, 0, IntPtr.Zero, (uint)targets.Length, targets, 0, IntPtr.Zero) == 0)
                RmShutdown(handle, 0, IntPtr.Zero);
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RestartProcess(int Id, System.Runtime.InteropServices.ComTypes.FILETIME Started);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint handle, uint fileCount, IntPtr files,
        uint processCount, [In] RestartProcess[] processes, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmShutdown(uint handle, uint flags, IntPtr callback);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint handle);
}
