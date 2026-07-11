using System.Diagnostics;

namespace DeskStreamer.Server.Service;

/// <summary>
/// Registers/unregisters a per-user logon autostart task via schtasks. This is deliberately a
/// scheduled task (SC ONLOGON) and NOT a session-0 Windows service: DXGI Desktop Duplication
/// cannot capture the interactive desktop from session 0, so DeskStream must run in the user's
/// own logon session.
/// </summary>
public static class Autostart
{
    private const string TaskName = "DeskStream";

    /// <summary>
    /// Creates the logon task. <paramref name="elevated"/> registers it to run at the highest
    /// available privileges (needed to inject input into elevated apps / capture UAC surfaces).
    /// </summary>
    public static int InstallAutostart(bool elevated)
    {
        string exe = ExecutablePath();
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("[autostart] could not determine the executable path.");
            return 1;
        }
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet",
                          StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "[autostart] install from the published DeskStreamer.Server.exe, not `dotnet run`.");
            return 1;
        }

        string runLevel = elevated ? "HIGHEST" : "LIMITED";
        string action = $"\"{exe}\" --headless";

        int code = RunSchtasks(
            "/Create", "/SC", "ONLOGON", "/IT", "/RL", runLevel,
            "/TN", TaskName, "/TR", action, "/F");

        if (code == 0)
            Console.WriteLine($"[autostart] installed logon task '{TaskName}' ({runLevel}) -> {action}");
        else
            Console.Error.WriteLine($"[autostart] install failed (exit {code}). Run from an elevated prompt if the task requires it.");
        return code;
    }

    /// <summary>Removes the logon task if present.</summary>
    public static int Uninstall()
    {
        int code = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (code == 0)
            Console.WriteLine($"[autostart] removed logon task '{TaskName}'.");
        else
            Console.Error.WriteLine($"[autostart] uninstall failed (exit {code}). The task may not exist.");
        return code;
    }

    private static string ExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static int RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine("[autostart] could not launch schtasks.exe.");
                return 1;
            }
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine(stderr.TrimEnd());
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[autostart] schtasks invocation failed: {ex.Message}");
            return 1;
        }
    }
}
