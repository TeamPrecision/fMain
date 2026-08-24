using fMain.Hubs;
using fMain.Models;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;

namespace fMain.Services;

/// <summary>
/// Manages per-head state and broadcasts changes via SignalR.
/// Phase 2 will add actual test execution here.
/// </summary>
public class HeadStateService
{
    private readonly Dictionary<int, HeadState> _heads = new();
    private readonly IHubContext<TestHub> _hub;
    private readonly ConfigService _cfg;
    private readonly object _lock = new();

    public HeadStateService(IHubContext<TestHub> hub, ConfigService cfg)
    {
        _hub = hub;
        _cfg = cfg;
        InitHeads(cfg.Config.Tester.NumHeads);
    }

    public void InitHeads(int count)
    {
        lock (_lock)
        {
            _heads.Clear();
            for (int i = 1; i <= count; i++)
                _heads[i] = new HeadState { HeadNumber = i };
        }
    }

    public List<HeadState> GetAll()
    {
        lock (_lock) return _heads.Values.OrderBy(h => h.HeadNumber).ToList();
    }

    public HeadState? Get(int head)
    {
        lock (_lock) { _heads.TryGetValue(head, out var h); return h; }
    }

    public async Task SetSerialNumber(int head, string sn)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.SerialNumber = sn;
        }
        await Push(head, state!);
    }

    public async Task SetWorkOrder(int head, string wo)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.WorkOrder = wo;
        }
        await Push(head, state!);
    }

    // Phase 2 will call these:
    public async Task SetStatus(int head, HeadStatus status)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.Status = status;
            if (status == HeadStatus.Testing) state.StartTime = DateTime.Now;
        }
        await Push(head, state!);
    }

    public async Task AddStepResult(int head, StepResult result)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.Steps.Add(result);
        }
        await Push(head, state!);
    }

    public async Task ClearHead(int head)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.Status = HeadStatus.Idle;
            state.SerialNumber = string.Empty;
            state.Progress = 0;
            state.TestTime = "00:00";
            state.InOutTime = "--:--";
            state.Steps.Clear();
            state.StartTime = null;
            state.CurrentStepIndex = -1;
            state.TotalSteps = 0;
        }
        await Push(head, state!);
    }

    // ── Phase 2: test execution helpers ──────────────────────────────────────

    public async Task InitTestSteps(int head, List<TestStep> planSteps)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            state.Steps.Clear();
            state.CurrentStepIndex = -1;
            state.TotalSteps = planSteps.Count(s => s.RowType != RowType.Header && s.RowType != RowType.Skip);
            state.Progress = 0;
            state.StartTime = DateTime.Now;
            state.TestTime = "00:00";

            foreach (var s in planSteps)
            {
                state.Steps.Add(new StepResult
                {
                    Step = s.Description,
                    Limit = FormatLimit(s.Min, s.Max, s.Unit),
                    Measure = "",
                    Result = s.RowType == RowType.Header ? "" : "—",
                    RowType = s.RowType.ToString(),
                    Function = s.Function
                });
            }
        }
        await Push(head, state!);
    }

    public async Task UpdateStep(int head, int stepIndex, string measure, string result, bool running, int durationMs = 0)
    {
        HeadState? state;
        lock (_lock)
        {
            if (!_heads.TryGetValue(head, out state)) return;
            if (stepIndex < 0 || stepIndex >= state.Steps.Count) return;

            state.Steps[stepIndex].Measure = measure;
            state.Steps[stepIndex].Result = result;
            if (durationMs > 0) state.Steps[stepIndex].DurationMs = durationMs;

            if (running)
            {
                state.CurrentStepIndex = stepIndex;
            }
            else
            {
                int done = state.Steps.Count(s =>
                    s.RowType != "Header" && s.Result != "—" && s.Result != "RUNNING");
                state.Progress = state.TotalSteps > 0
                    ? Math.Round((double)done / state.TotalSteps * 100, 1)
                    : 0;

                if (state.StartTime.HasValue)
                {
                    var e = DateTime.Now - state.StartTime.Value;
                    state.TestTime = $"{(int)e.TotalMinutes:D2}:{e.Seconds:D2}";
                }
            }
        }
        await Push(head, state!);
    }

    private static string FormatLimit(string min, string max, string unit)
    {
        string u = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        bool hasMin = !string.IsNullOrEmpty(min);
        bool hasMax = !string.IsNullOrEmpty(max);
        if (!hasMin && !hasMax) return "";
        if (!hasMin) return $"≤{max}{u}";
        if (!hasMax) return $"≥{min}{u}";
        return $"{min}~{max}{u}";
    }

    private Task Push(int head, HeadState state) =>
        _hub.Clients.All.SendAsync("HeadUpdate", head, state);
}
