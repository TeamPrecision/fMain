using System.Text.Json;
using System.Text.Json.Serialization;
using fMain.Models;

namespace fMain.Services;

public class TestPlanService
{
    private TestPlan _sharedPlan = new();
    private readonly Dictionary<int, string> _headOverrides = new();   // headNum → file path
    private readonly object _lock = new();
    private readonly ILogger<TestPlanService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public TestPlanService(ILogger<TestPlanService> logger) => _logger = logger;

    // ── Shared plan ───────────────────────────────────────────────────────────

    public TestPlan SharedPlan
    {
        get { lock (_lock) return _sharedPlan; }
    }

    public void SetSharedPlan(TestPlan plan)
    {
        RenumberSteps(plan);
        lock (_lock) _sharedPlan = plan;
    }

    // ── Per-head overrides ────────────────────────────────────────────────────

    public TestPlan GetPlanForHead(int headNum)
    {
        lock (_lock)
        {
            if (_headOverrides.TryGetValue(headNum, out var path))
            {
                try { return LoadFromFile(path); }
                catch (Exception ex)
                {
                    _logger.LogWarning("Head {Head} plan override '{Path}' failed: {Err}", headNum, path, ex.Message);
                }
            }
            return _sharedPlan;
        }
    }

    public void SetHeadOverride(int headNum, string? filePath)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(filePath)) _headOverrides.Remove(headNum);
            else _headOverrides[headNum] = filePath;
        }
    }

    public Dictionary<int, string> GetOverrides()
    {
        lock (_lock) return new Dictionary<int, string>(_headOverrides);
    }

    // ── File I/O ──────────────────────────────────────────────────────────────

    public async Task<TestPlan> LoadAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var plan = JsonSerializer.Deserialize<TestPlan>(json, JsonOpts)
                   ?? throw new InvalidOperationException("Empty plan file");
        plan.FilePath = filePath;
        RenumberSteps(plan);
        return plan;
    }

    public async Task SaveAsync(TestPlan plan, string filePath)
    {
        RenumberSteps(plan);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(plan, JsonOpts));
        plan.FilePath = filePath;
    }

    private static TestPlan LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var plan = JsonSerializer.Deserialize<TestPlan>(json, JsonOpts) ?? new();
        plan.FilePath = filePath;
        return plan;
    }

    public static void RenumberSteps(TestPlan plan)
    {
        int n = 1;
        foreach (var s in plan.Steps)
            s.StepNum = s.RowType == RowType.Header ? 0 : n++;
    }
}
