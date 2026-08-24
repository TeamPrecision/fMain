using System.Reflection;
using fMain.Models;

namespace fMain.Services;

/// <summary>
/// Wraps TeamPrecision.PRISM.dll via reflection.
/// In Debug mode all calls are no-ops that return success.
/// </summary>
public class PrismService
{
    // Static accessor for Roslyn-compiled plugin modules.
    public static PrismService? Instance { get; private set; }

    private readonly ConfigService _cfg;
    private readonly ILogger<PrismService> _logger;
    private Assembly? _asm;
    private bool _loaded;

    public PrismService(ConfigService cfg, ILogger<PrismService> logger)
    {
        _cfg = cfg;
        _logger = logger;
        Instance = this;
        TryLoad();
    }

    public bool IsDebugMode => !string.Equals(_cfg.Config.Prism.Mode, "Operation", StringComparison.OrdinalIgnoreCase);
    public bool IsLoaded => _loaded;
    public string Mode => _cfg.Config.Prism.Mode;

    private void TryLoad()
    {
        if (IsDebugMode)
        {
            _logger.LogInformation("PrismService: Debug mode — DLL calls bypassed");
            return;
        }

        var path = _cfg.Config.Prism.DllPath;
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("PrismService: Operation mode but DllPath not configured");
            return;
        }
        if (!File.Exists(path))
        {
            _logger.LogWarning("PrismService: DLL not found at {Path}", path);
            return;
        }

        try
        {
            // Load all DLLs in the same folder (dependencies: TeamPrecision.AD.dll, TeamPrecision.DAL.dll)
            var dir = Path.GetDirectoryName(path)!;
            foreach (var dep in Directory.GetFiles(dir, "*.dll"))
            {
                if (!dep.Equals(path, StringComparison.OrdinalIgnoreCase))
                    try { Assembly.LoadFrom(dep); } catch { /* best effort */ }
            }

            _asm = Assembly.LoadFrom(path);

            // ReadSetting() populates cSettingValues static fields from TeamPrecision.PRISM.Setting.xml
            InvokeStatic("TeamPrecision.PRISM.cSettings", "ReadSetting");
            _loaded = true;

            _logger.LogInformation("PrismService: DLL loaded — ProcessName={Proc}, TestingMode={Mode}",
                GetField("TeamPrecision.PRISM.cSettingValues", "ProcessName"),
                GetField("TeamPrecision.PRISM.cSettingValues", "TestingMode"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrismService: failed to load Prism DLL — falling back to Debug behaviour");
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string GetProcessName()
    {
        if (!_loaded) return _cfg.Config.Prism.ProcessName;
        return GetField("TeamPrecision.PRISM.cSettingValues", "ProcessName") ?? _cfg.Config.Prism.ProcessName;
    }

    public string GetEmployeeId()
    {
        if (!_loaded) return "";
        return GetField("TeamPrecision.PRISM.cSettingValues", "EmployeeID") ?? "";
    }

    public bool ValidateEmployeeId(string employeeId)
    {
        if (IsDebugMode) return true;
        if (!_loaded) return false;  // DLL configured but not loaded = deny in Operation mode
        // Re-read PRISM settings so we pick up the current operator login
        try { InvokeStatic("TeamPrecision.PRISM.cSettings", "ReadSetting"); } catch { }
        var stored = GetEmployeeId();
        return !string.IsNullOrEmpty(stored) && stored.Equals(employeeId, StringComparison.Ordinal);
    }

    /// <summary>Returns "SUCCESS" or error description.</summary>
    public string SaveTestResult(string sn, string passFail, string testResult)
    {
        if (IsDebugMode || !_loaded) return $"SUCCESS(Debug)";
        try
        {
            return InvokeStatic("TeamPrecision.PRISM.cResults", "SaveTestResult", sn, passFail, testResult)?.ToString()
                   ?? "NULL_RESPONSE";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrismService.SaveTestResult failed");
            return "ERROR:" + ex.Message;
        }
    }

    /// <summary>
    /// Returns the raw getWO string array (index 4 = process qty) or null.
    /// </summary>
    public string[]? GetWorkOrderInfo(string workOrder)
    {
        if (IsDebugMode || !_loaded) return null;
        try
        {
            return InvokeStatic("TeamPrecision.PRISM.cSNs", "getWO", workOrder, GetProcessName()) as string[];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrismService.GetWorkOrderInfo failed");
            return null;
        }
    }

    /// <summary>Validates an SN against the MES. Returns (valid, message).</summary>
    public (bool valid, string message) ValidateSN(string sn, string workOrder)
    {
        if (IsDebugMode) return (true, "Debug");
        if (!_loaded) return (false, "PRISM_NOT_LOADED");
        try
        {
            var result = InvokeStatic("TeamPrecision.PRISM.cSNs", "sn_check_valid",
                             sn, workOrder, GetProcessName(), false) as string[];
            if (result == null || result.Length == 0) return (false, "NO_RESPONSE");
            bool ok = result[0].StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase)
                   || result[0].Equals("PASS", StringComparison.OrdinalIgnoreCase);
            return (ok, result[0]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrismService.ValidateSN failed");
            return (false, "ERROR:" + ex.Message);
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private object? InvokeStatic(string typeName, string methodName, params object[] args)
    {
        var type = _asm?.GetType(typeName)
            ?? throw new InvalidOperationException($"Type {typeName} not found in Prism DLL");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args.Length == 0 ? null : args);
    }

    private string? GetField(string typeName, string fieldName)
    {
        var type = _asm?.GetType(typeName);
        if (type == null) return null;
        return type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
                   ?.GetValue(null)?.ToString();
    }
}
