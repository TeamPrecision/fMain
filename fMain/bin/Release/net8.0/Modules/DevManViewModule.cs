// COM port enable/disable via DevManView.exe (NirSoft).
// DevManViewPath is set at startup from fmain_config.json Hardware.DevManViewPath.
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;
using System.Diagnostics;

[FMainModule("DevManView", "Enable / disable COM port devices via DevManView.exe", "System", "1.0.0")]
public class DevManViewModule
{
    // Set at startup from HardwareConfig.DevManViewPath
    public static string DevManViewPath { get; set; } = "devmanview.exe";

    [FMainFunction("Disable a COM port in Device Manager (e.g. portName=COM3)", "System")]
    public void comport_disable(string portName, string devmanPath = "")
    {
        Run("/disable", portName, devmanPath);
    }

    [FMainFunction("Enable a COM port in Device Manager (e.g. portName=COM3)", "System")]
    public void comport_enable(string portName, string devmanPath = "")
    {
        Run("/enable", portName, devmanPath);
    }

    private static void Run(string action, string portName, string pathOverride)
    {
        var ctx = TestContext.Current.Value;
        var exe = !string.IsNullOrEmpty(pathOverride) ? pathOverride : DevManViewPath;
        try
        {
            var psi = new ProcessStartInfo(exe, $"{action} \"{portName}\"")
            {
                UseShellExecute      = false,
                CreateNoWindow       = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var proc = Process.Start(psi)!;
            bool exited = proc.WaitForExit(5000);
            if (ctx != null) ctx.MeasureValue = exited ? $"exit={proc.ExitCode}" : "TIMEOUT";
        }
        catch (Exception ex)
        {
            if (ctx != null) { ctx.MeasureValue = $"Error: {ex.Message}"; ctx.ForceResult = false; }
        }
    }
}
