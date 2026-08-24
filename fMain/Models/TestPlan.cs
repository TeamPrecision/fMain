namespace fMain.Models;

public enum RowType { Normal, Header, Skip, Serial }
public enum FailBehavior { Stop, ContinueCells, ContinueAll }

public class TestStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public RowType RowType { get; set; } = RowType.Normal;
    public int StepNum { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Param1 { get; set; } = string.Empty;
    public string Param2 { get; set; } = string.Empty;
    public string Param3 { get; set; } = string.Empty;
    public string Param4 { get; set; } = string.Empty;
    public string Min { get; set; } = string.Empty;
    public string Max { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public FailBehavior FailBehavior { get; set; } = FailBehavior.Stop;
    public int TimeoutMs { get; set; } = 30000;
    // Flow control: "next" | "end" | stepId — used by block diagram editor
    public string OnPassGoto { get; set; } = "next";
    public string OnFailGoto { get; set; } = "end";
}

public class TestPlan
{
    public string Name { get; set; } = "Untitled Plan";
    public string Version { get; set; } = "1.0";
    public string FilePath { get; set; } = string.Empty;
    public bool SameStepMode { get; set; }
    public int DefaultTimeoutMs { get; set; } = 30000;
    public List<TestStep> Steps { get; set; } = new();
}

// Helper request DTOs for API
public class LoadPlanRequest  { public string FilePath { get; set; } = string.Empty; }
public class SavePlanRequest  { public TestPlan Plan { get; set; } = new(); public string FilePath { get; set; } = string.Empty; }
public class HeadOverrideRequest { public int HeadNum { get; set; } public string? FilePath { get; set; } }
public class WORequest { public int Head { get; set; } = -1; public string? WorkOrder { get; set; } }
