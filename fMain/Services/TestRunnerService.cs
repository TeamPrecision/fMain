using System.Reflection;
using fMain.Hubs;
using fMain.Models;
using Microsoft.AspNetCore.SignalR;

namespace fMain.Services;

public class TestRunnerService
{
    private readonly HeadStateService _heads;
    private readonly ModuleLoaderService _modules;
    private readonly TestPlanService _plans;
    private readonly DatalogService _datalog;
    private readonly WorkOrderService _wo;
    private readonly ConfigService _cfg;
    private readonly IHubContext<TestHub> _hub;
    private readonly ILogger<TestRunnerService> _logger;

    private readonly Dictionary<int, CancellationTokenSource> _cts = new();
    private readonly object _lock = new();

    public TestRunnerService(
        HeadStateService heads,
        ModuleLoaderService modules,
        TestPlanService plans,
        DatalogService datalog,
        WorkOrderService wo,
        ConfigService cfg,
        IHubContext<TestHub> hub,
        ILogger<TestRunnerService> logger)
    {
        _heads = heads;
        _modules = modules;
        _plans = plans;
        _datalog = datalog;
        _wo = wo;
        _cfg = cfg;
        _hub = hub;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRunning(int head)
    {
        lock (_lock)
            return _cts.TryGetValue(head, out var c) && !c.IsCancellationRequested;
    }

    public Task StartAsync(int headNum, int fromStep = 0)
    {
        Cancel(headNum);
        var plan = _plans.GetPlanForHead(headNum);
        var cts = new CancellationTokenSource();
        lock (_lock) _cts[headNum] = cts;
        _ = Task.Run(() => RunHead(headNum, plan, fromStep, cts.Token));
        return Task.CompletedTask;
    }

    public Task StartAllAsync()
    {
        var heads = _heads.GetAll();
        foreach (var h in heads)
            StartAsync(h.HeadNumber);
        return Task.CompletedTask;
    }

    public async Task StopAsync(int headNum)
    {
        Cancel(headNum);
        var state = _heads.Get(headNum);
        if (state?.Status == HeadStatus.Testing)
            await _heads.SetStatus(headNum, HeadStatus.Stopped);
    }

    public async Task StopAllAsync()
    {
        int[] running;
        lock (_lock) running = _cts.Keys.ToArray();
        foreach (var h in running) Cancel(h);

        foreach (var h in _heads.GetAll())
            if (h.Status == HeadStatus.Testing)
                await _heads.SetStatus(h.HeadNumber, HeadStatus.Stopped);
    }

    // ── Execution loop ────────────────────────────────────────────────────────

    private async Task RunHead(int headNum, TestPlan plan, int fromStep, CancellationToken ct)
    {
        await _heads.SetStatus(headNum, HeadStatus.Testing);
        await _heads.InitTestSteps(headNum, plan.Steps);

        bool anyFail = false;
        bool skipUntilHeader = false;  // for ContinueCells

        // Build stepId→index map for OnPassGoto/OnFailGoto jumps
        var idxById = new Dictionary<string, int>();
        for (int k = 0; k < plan.Steps.Count; k++)
            if (!string.IsNullOrEmpty(plan.Steps[k].Id))
                idxById[plan.Steps[k].Id] = k;

        int i = fromStep;
        while (i < plan.Steps.Count)
        {
            if (ct.IsCancellationRequested) break;

            var step = plan.Steps[i];

            if (step.RowType == RowType.Header)
            {
                skipUntilHeader = false;
                i++; continue;
            }

            if (step.RowType == RowType.Skip || skipUntilHeader)
            {
                await _heads.UpdateStep(headNum, i, "SKIP", "SKIP", running: false);
                i++; continue;
            }

            await _heads.UpdateStep(headNum, i, "…", "RUNNING", running: true);

            var stepStart = DateTime.Now;
            var (measure, result) = await ExecuteStepAsync(step, plan.DefaultTimeoutMs, ct);
            int durationMs = (int)(DateTime.Now - stepStart).TotalMilliseconds;

            if (ct.IsCancellationRequested) break;

            await _heads.UpdateStep(headNum, i, measure, result, running: false, durationMs);

            bool passed = result == "PASS";
            if (!passed) anyFail = true;

            // Resolve goto: OnPassGoto / OnFailGoto take precedence over FailBehavior
            string goto_ = passed ? step.OnPassGoto : step.OnFailGoto;

            if (goto_ == "end" || (goto_ == "next" && !passed && step.FailBehavior == FailBehavior.Stop))
                break;

            if (goto_ != "next")
            {
                if (idxById.TryGetValue(goto_, out var target))
                    { i = target; continue; }
                // Unknown target — treat as end
                break;
            }

            // goto_ == "next" with FailBehavior handling
            if (!passed && step.FailBehavior == FailBehavior.ContinueCells)
                skipUntilHeader = true;

            i++;
        }

        var endTime = DateTime.Now;

        if (!ct.IsCancellationRequested)
        {
            await _heads.SetStatus(headNum, anyFail ? HeadStatus.Fail : HeadStatus.Pass);

            // Save MySQL datalog
            var finalState = _heads.Get(headNum);
            if (finalState != null)
            {
                var logId = await _datalog.SaveAsync(finalState, plan, endTime);
                if (logId > 0)
                    await _hub.Clients.All.SendAsync("DatalogSaved", logId, headNum,
                        finalState.SerialNumber, !anyFail ? "PASS" : "FAIL");
            }

            // Record WO stats
            _wo.RecordTestComplete(headNum, !anyFail);
            await _hub.Clients.All.SendAsync("WorkOrderUpdated", headNum,
                _heads.Get(headNum)?.WorkOrder ?? "", _wo.GetEntry(headNum));

            // Auto-clear SN after test
            if (_cfg.Config.Tester.AutoClearSnAfterTest)
                await _heads.SetSerialNumber(headNum, string.Empty);
        }
    }

    // ── Step execution ────────────────────────────────────────────────────────

    private async Task<(string measure, string result)> ExecuteStepAsync(
        TestStep step, int defaultTimeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(step.Function))
            return ("—", "PASS");  // empty function = no-op step, auto-pass

        int timeout = step.TimeoutMs > 0 ? step.TimeoutMs : defaultTimeout;
        var ctx = new TestContext { CancellationToken = ct };
        TestContext.Current.Value = ctx;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await Task.Run(() =>
                _modules.InvokeFunction(step.Function, step.Param1, step.Param2, step.Param3, step.Param4),
                linked.Token);

            if (ctx.ForceResult.HasValue)
                return (ctx.MeasureValue, ctx.ForceResult.Value ? "PASS" : "FAIL");

            return (ctx.MeasureValue, CheckLimits(step.Min, step.Max, ctx.MeasureValue));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("TIMEOUT", "FAIL");
        }
        catch (OperationCanceledException)
        {
            return ("ABORTED", "FAIL");
        }
        catch (TargetInvocationException tie)
        {
            var msg = tie.InnerException?.Message ?? tie.Message;
            _logger.LogWarning("Step '{Fn}' threw: {Err}", step.Function, msg);
            return (msg, "FAIL");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Step '{Fn}' error: {Err}", step.Function, ex.Message);
            return (ex.Message, "FAIL");
        }
        finally
        {
            TestContext.Current.Value = null;
        }
    }

    private static string CheckLimits(string min, string max, string measure)
    {
        bool hasMin = !string.IsNullOrEmpty(min);
        bool hasMax = !string.IsNullOrEmpty(max);
        if (!hasMin && !hasMax) return "PASS";

        if (double.TryParse(measure, out var m))
        {
            bool okLo = !hasMin || (double.TryParse(min, out var lo) && m >= lo);
            bool okHi = !hasMax || (double.TryParse(max, out var hi) && m <= hi);
            return (okLo && okHi) ? "PASS" : "FAIL";
        }

        // String comparison: Min field is used as expected value
        return (hasMin && measure == min) ? "PASS" : "FAIL";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Cancel(int headNum)
    {
        lock (_lock)
        {
            if (_cts.TryGetValue(headNum, out var old))
            {
                old.Cancel();
                old.Dispose();
                _cts.Remove(headNum);
            }
        }
    }
}
