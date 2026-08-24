namespace fMain.Models;

// ─── Access Control ───────────────────────────────────────────────────────────

public enum SessionState { Connecting, Monitor, Control, PendingControl }
public enum ControlRequestStatus { Pending, Approved, Denied, Cancelled }

public class ClientSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ConnectionId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SessionState State { get; set; } = SessionState.Monitor;
    public DateTime ConnectedAt { get; set; } = DateTime.Now;
    public bool IsAdmin { get; set; }
    public bool IsSpecialIp { get; set; }
}

public class ControlRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ConnectionId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public ControlRequestStatus Status { get; set; } = ControlRequestStatus.Pending;
}

public class SessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsSpecialIp { get; set; }
    public DateTime ConnectedAt { get; set; }
}

// ─── Head / Test State ───────────────────────────────────────────────────────

public enum HeadStatus { Idle, Waiting, Testing, Pass, Fail, Stopped }

public class HeadState
{
    public int HeadNumber { get; set; }
    public HeadStatus Status { get; set; } = HeadStatus.Idle;
    public string SerialNumber { get; set; } = string.Empty;
    public string WorkOrder { get; set; } = string.Empty;
    public double Progress { get; set; }
    public string TestTime { get; set; } = "00:00";
    public string InOutTime { get; set; } = "--:--";
    public List<StepResult> Steps { get; set; } = new();
    public DateTime? StartTime { get; set; }
    public int CurrentStepIndex { get; set; } = -1;
    public int TotalSteps { get; set; }
}

public class StepResult
{
    public string Step { get; set; } = string.Empty;
    public string Limit { get; set; } = string.Empty;
    public string Measure { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string RowType { get; set; } = "Normal";  // Normal, Header, Skip, Serial
    public string Function { get; set; } = string.Empty;
    public int DurationMs { get; set; }
}

// ─── Module / Plugin ─────────────────────────────────────────────────────────

public class ModuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string SourcePath { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public bool IsLoaded { get; set; }
    public string? LoadError { get; set; }
    public DateTime LoadedAt { get; set; } = DateTime.Now;
    public List<FunctionInfo> Functions { get; set; } = new();
}

public class FunctionInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ReturnType { get; set; } = "void";
    public List<ParamInfo> Parameters { get; set; } = new();
}

public class ParamInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public string Description { get; set; } = string.Empty;
}

// ─── Configuration ───────────────────────────────────────────────────────────

public class FMainConfig
{
    public int Port { get; set; } = 49600;
    public List<string> SpecialIPs { get; set; } = new() { "192.168.10.112" };
    public List<string> AdminIPs { get; set; } = new() { "127.0.0.1", "::1" };
    public string ModulesBasePath { get; set; } = "D:/svn";
    public TesterConfig Tester { get; set; } = new();
    public MySqlConfig MySql { get; set; } = new();
    public ScriptConfig Script { get; set; } = new();
    public UIConfig UI { get; set; } = new();
    public HardwareConfig Hardware { get; set; } = new();
    public PrismConfig Prism { get; set; } = new();
}

public class HardwareConfig
{
    public string DevManViewPath { get; set; } = string.Empty;
    public string VisaDefaultResource { get; set; } = string.Empty;
    public int VisaTimeoutMs { get; set; } = 5000;
}

public class TesterConfig
{
    public int NumHeads { get; set; } = 1;
    public string FailBehavior { get; set; } = "Stop";
    public bool AllowRetest { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public bool UseRelayCard { get; set; } = false;
    public string RelayCardComport { get; set; } = string.Empty;
    public int NumCardRelay { get; set; } = 1;
    public bool AutoClearSnAfterTest { get; set; } = false;
    public string SnValidationRegex { get; set; } = string.Empty;
    public string FgPlansFolder { get; set; } = string.Empty;
}

public class MySqlConfig
{
    public string ConnectionString { get; set; } = "Server=localhost;Port=3306;Database=fmain_db;User=root;Password=;";
}

public class PrismConfig
{
    public string DllPath { get; set; } = string.Empty;
    public string Mode { get; set; } = "Debug";
    public string ProcessName { get; set; } = "FCT";
    public string ComputerName { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public int SnDigits { get; set; } = 0;
}

public class ScriptConfig
{
    public string PyVersion { get; set; } = "Python313";
    public string PyFolder { get; set; } = string.Empty;
}

public class UIConfig
{
    public string BarcodeMode { get; set; } = "Scanner";
}
