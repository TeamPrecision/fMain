using fMain.Models;
using fMain.Services;
using Microsoft.AspNetCore.Mvc;

namespace fMain.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly ConfigService _cfg;
    private readonly ModuleLoaderService _modules;
    private readonly AccessControlService _access;
    private readonly HeadStateService _heads;
    private readonly TestPlanService _plans;
    private readonly DatalogService _datalog;
    private readonly WorkOrderService _wo;
    private readonly PrismService _prism;

    public ApiController(ConfigService cfg, ModuleLoaderService modules,
        AccessControlService access, HeadStateService heads, TestPlanService plans,
        DatalogService datalog, WorkOrderService wo, PrismService prism)
    {
        _cfg = cfg;
        _modules = modules;
        _access = access;
        _heads = heads;
        _plans = plans;
        _datalog = datalog;
        _wo = wo;
        _prism = prism;
    }

    // ── Config ────────────────────────────────────────────────────────────────

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(_cfg.Config);

    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] FMainConfig config)
    {
        _cfg.Update(config);
        _heads.InitHeads(config.Tester.NumHeads);
        return Ok(new { message = "Saved" });
    }

    // ── Modules ───────────────────────────────────────────────────────────────

    [HttpGet("modules")]
    public IActionResult GetModules() => Ok(_modules.GetAll());

    [HttpPost("modules/reload")]
    public async Task<IActionResult> ReloadModules()
    {
        await _modules.ScanAndLoadAsync();
        return Ok(_modules.GetAll());
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        Version = "1.0.0-phase5",
        StartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime,
        Sessions = _access.GetSessions().Count,
        Controller = _access.ControllerConnectionId != null,
        Modules = _modules.GetAll().Count(m => m.IsLoaded),
        Heads = _heads.GetAll().Count
    });

    // ── Heads ─────────────────────────────────────────────────────────────────

    [HttpGet("heads")]
    public IActionResult GetHeads() => Ok(_heads.GetAll());

    // ── Test plan ─────────────────────────────────────────────────────────────

    [HttpGet("plan")]
    public IActionResult GetPlan() => Ok(_plans.SharedPlan);

    [HttpPost("plan")]
    public IActionResult SetPlan([FromBody] TestPlan plan)
    {
        _plans.SetSharedPlan(plan);
        return Ok(_plans.SharedPlan);
    }

    [HttpPost("plan/load")]
    public async Task<IActionResult> LoadPlan([FromBody] LoadPlanRequest req)
    {
        if (!System.IO.File.Exists(req.FilePath))
            return NotFound(new { error = $"File not found: {req.FilePath}" });
        try
        {
            var plan = await _plans.LoadAsync(req.FilePath);
            _plans.SetSharedPlan(plan);
            return Ok(_plans.SharedPlan);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("plan/save")]
    public async Task<IActionResult> SavePlan([FromBody] SavePlanRequest req)
    {
        try
        {
            await _plans.SaveAsync(req.Plan, req.FilePath);
            return Ok(new { message = "Saved", filePath = req.FilePath });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("plan/overrides")]
    public IActionResult GetOverrides() => Ok(_plans.GetOverrides());

    [HttpPost("plan/override")]
    public IActionResult SetOverride([FromBody] HeadOverrideRequest req)
    {
        _plans.SetHeadOverride(req.HeadNum, req.FilePath);
        return Ok(new { message = "Override set" });
    }

    // ── Datalog ───────────────────────────────────────────────────────────────

    [HttpGet("datalog")]
    public async Task<IActionResult> QueryDatalog(
        [FromQuery] string? sn, [FromQuery] string? wo,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int limit = 100)
    {
        DateTime? dtFrom = string.IsNullOrEmpty(from) ? null : DateTime.TryParse(from, out var f) ? f : null;
        DateTime? dtTo   = string.IsNullOrEmpty(to)   ? null : DateTime.TryParse(to,   out var t) ? t : null;
        var result = await _datalog.QueryAsync(sn, wo, dtFrom, dtTo, limit);
        return Ok(result);
    }

    [HttpGet("datalog/{logId:long}/steps")]
    public async Task<IActionResult> QuerySteps(long logId)
    {
        var result = await _datalog.QueryStepsAsync(logId);
        return Ok(result);
    }

    [HttpGet("db/test")]
    public async Task<IActionResult> TestDb()
    {
        var ok = await _datalog.TestConnectionAsync();
        return Ok(new { connected = ok, message = ok ? "Connected" : "Failed" });
    }

    // ── Work Order ────────────────────────────────────────────────────────────

    [HttpGet("workorder")]
    public IActionResult GetWorkOrders() => Ok(_wo.GetAll());

    [HttpGet("workorder/{head:int}")]
    public IActionResult GetWorkOrder(int head) =>
        Ok(new { head, workOrder = _wo.GetWorkOrder(head), info = _wo.GetEntry(head) });

    [HttpPost("workorder")]
    public async Task<IActionResult> SetWorkOrder([FromBody] WORequest req)
    {
        if (req.Head < 0)
            await _wo.SetWorkOrderAllAsync(req.WorkOrder ?? "");
        else
            await _wo.SetWorkOrderAsync(req.Head, req.WorkOrder ?? "");
        return Ok(new { message = "OK" });
    }

    // ── Stats / CPK ───────────────────────────────────────────────────────────

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] string? wo, [FromQuery] string? step,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        DateTime? dtFrom = string.IsNullOrEmpty(from) ? null : DateTime.TryParse(from, out var f) ? f : null;
        DateTime? dtTo   = string.IsNullOrEmpty(to)   ? null : DateTime.TryParse(to,   out var t) ? t : null;
        return Ok(await _datalog.QueryStatsAsync(wo, step, dtFrom, dtTo));
    }

    [HttpGet("trend")]
    public async Task<IActionResult> GetTrend(
        [FromQuery] string step,
        [FromQuery] string? wo, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int limit = 200)
    {
        DateTime? dtFrom = string.IsNullOrEmpty(from) ? null : DateTime.TryParse(from, out var f) ? f : null;
        DateTime? dtTo   = string.IsNullOrEmpty(to)   ? null : DateTime.TryParse(to,   out var t) ? t : null;
        return Ok(await _datalog.QueryTrendAsync(step, wo, dtFrom, dtTo, limit));
    }

    // ── FG Plans ──────────────────────────────────────────────────────────────

    [HttpGet("fg-plans")]
    public IActionResult GetFgPlans()
    {
        var folder = _cfg.Config.Tester.FgPlansFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return Ok(new List<object>());

        var files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .Select(f => new { name = Path.GetFileNameWithoutExtension(f), path = f })
            .OrderBy(x => x.name)
            .ToList();
        return Ok(files);
    }

    // ── Employee Validation ───────────────────────────────────────────────────

    [HttpGet("validate-employee")]
    public IActionResult ValidateEmployee([FromQuery] string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 5 || !id.All(char.IsDigit))
            return Ok(new { valid = false, message = "Employee ID must be exactly 5 digits" });

        if (_prism.IsDebugMode)
            return Ok(new { valid = true, message = "Debug mode" });

        if (!_prism.IsLoaded)
            return Ok(new { valid = false, message = "PRISM DLL not loaded — check DLL path in Settings > Prism" });

        bool valid = _prism.ValidateEmployeeId(id);
        return Ok(new { valid, message = valid ? "OK" : "Employee ID not recognised by PRISM. Please log in to PRISM first." });
    }

    // ── User Login (PRISM cUsers.UserLogin) ──────────────────────────────────

    [HttpGet("user-login")]
    public IActionResult UserLogin([FromQuery] string id)
    {
        if (string.IsNullOrEmpty(id))
            return Ok(new { ok = false, result = "false" });
        var (ok, result) = _prism.UserLogin(id);
        return Ok(new { ok, result });
    }

    // ── Sessions (admin) ──────────────────────────────────────────────────────

    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        if (!_cfg.IsAdminIp(GetIp())) return Forbid();
        return Ok(_access.GetSessions());
    }

    private string GetIp()
    {
        var fwd = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fwd)) return fwd.Split(',')[0].Trim();
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
