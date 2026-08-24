using fMain.Models;
using fMain.Services;
using Microsoft.AspNetCore.SignalR;

namespace fMain.Hubs;

public class TestHub : Hub
{
    private readonly AccessControlService _access;
    private readonly ConfigService _cfg;
    private readonly ModuleLoaderService _modules;
    private readonly HeadStateService _heads;
    private readonly TestPlanService _plans;
    private readonly TestRunnerService _runner;
    private readonly WorkOrderService _wo;
    private readonly ILogger<TestHub> _logger;

    private const string GroupAdmins = "admins";

    public TestHub(
        AccessControlService access,
        ConfigService cfg,
        ModuleLoaderService modules,
        HeadStateService heads,
        TestPlanService plans,
        TestRunnerService runner,
        WorkOrderService wo,
        ILogger<TestHub> logger)
    {
        _access = access;
        _cfg = cfg;
        _modules = modules;
        _heads = heads;
        _plans = plans;
        _runner = runner;
        _wo = wo;
        _logger = logger;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var ip = GetClientIp();
        if (_cfg.IsAdminIp(ip))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupAdmins);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var wasController = _access.ControllerConnectionId == Context.ConnectionId;
        _access.Remove(Context.ConnectionId);

        if (wasController)
            await Clients.All.SendAsync("ControlReleased", Context.ConnectionId);

        await Clients.All.SendAsync("SessionLeft", Context.ConnectionId);
        await PushSessionList();
        await base.OnDisconnectedAsync(exception);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Called by client on connect. Returns session info + initial state.</summary>
    public async Task Register(string displayName, string requestedMode)
    {
        var ip = GetClientIp();
        var session = _access.Register(Context.ConnectionId, ip, displayName);

        await Clients.Caller.SendAsync("Registered", new
        {
            session.Id,
            session.IpAddress,
            session.IsAdmin,
            session.IsSpecialIp,
            State = session.State.ToString(),
            Config = new
            {
                _cfg.Config.Tester.NumHeads,
                _cfg.Config.Tester.FailBehavior,
                _cfg.Config.Tester.AllowRetest,
                _cfg.Config.Tester.AutoStart
            }
        });

        // Push all current head states and the current plan to the new client
        foreach (var head in _heads.GetAll())
            await Clients.Caller.SendAsync("HeadUpdate", head.HeadNumber, head);

        await Clients.Caller.SendAsync("PlanLoaded", _plans.SharedPlan);
        await PushSessionList();

        if (requestedMode == "control")
            await RequestControl();
    }

    // ── Access control ────────────────────────────────────────────────────────

    public async Task RequestControl()
    {
        var session = _access.GetSession(Context.ConnectionId);
        if (session == null) { await Clients.Caller.SendAsync("Error", "Not registered"); return; }

        var (granted, requestId, message) = _access.RequestControl(Context.ConnectionId);

        if (granted)
        {
            await Clients.Caller.SendAsync("ControlGranted");
            await Clients.All.SendAsync("ControlChanged", Context.ConnectionId, session.DisplayName, session.IpAddress);
        }
        else if (requestId != null)
        {
            await Clients.Caller.SendAsync("ControlPending", requestId);
            // Notify admins so they see the approval popup
            await Clients.Group(GroupAdmins).SendAsync("ControlRequested", new
            {
                RequestId = requestId,
                session.DisplayName,
                session.IpAddress
            });
        }
        else
        {
            await Clients.Caller.SendAsync("ControlDenied", message);
        }

        await PushSessionList();
    }

    public async Task ApproveControl(string requestId)
    {
        if (!IsAdmin()) { await Clients.Caller.SendAsync("Error", "Not authorized"); return; }

        var (ok, grantedConnId) = _access.Approve(requestId);
        if (!ok) return;

        var session = _access.GetSession(grantedConnId!);
        await Clients.Client(grantedConnId!).SendAsync("ControlGranted");
        await Clients.All.SendAsync("ControlChanged", grantedConnId,
            session?.DisplayName ?? "", session?.IpAddress ?? "");
        await PushSessionList();
    }

    public async Task DenyControl(string requestId)
    {
        if (!IsAdmin()) { await Clients.Caller.SendAsync("Error", "Not authorized"); return; }

        var pending = _access.GetPendingRequests().FirstOrDefault(r => r.RequestId == requestId);
        var (ok, deniedConnId) = _access.Deny(requestId);
        if (!ok) return;

        await Clients.Client(deniedConnId!).SendAsync("ControlDenied", "Denied by server");
        await PushSessionList();
    }

    public async Task ReleaseControl()
    {
        _access.Release(Context.ConnectionId);
        await Clients.Caller.SendAsync("ControlReleased", Context.ConnectionId);
        await Clients.All.SendAsync("ControlReleased", Context.ConnectionId);
        await PushSessionList();
    }

    // ── Head control ──────────────────────────────────────────────────────────

    public async Task SetSerialNumber(int head, string sn)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _heads.SetSerialNumber(head, sn);
    }

    public async Task ClearHead(int head)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _heads.ClearHead(head);
    }

    // ── Work Order ────────────────────────────────────────────────────────────

    public async Task SetWorkOrder(int head, string wo)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _wo.SetWorkOrderAsync(head, wo);
        await Clients.All.SendAsync("WorkOrderUpdated", head, wo, _wo.GetEntry(head));
    }

    public async Task SetWorkOrderAll(string wo)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _wo.SetWorkOrderAllAsync(wo);
        await Clients.All.SendAsync("WorkOrderUpdated", -1, wo, null);
    }

    public async Task GetWorkOrder(int head)
    {
        await Clients.Caller.SendAsync("WorkOrderInfo", head, _wo.GetWorkOrder(head), _wo.GetEntry(head));
    }

    // ── Test plan ─────────────────────────────────────────────────────────────

    public async Task GetPlan() =>
        await Clients.Caller.SendAsync("PlanLoaded", _plans.SharedPlan);

    public async Task SetPlan(TestPlan plan)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        _plans.SetSharedPlan(plan);
        await Clients.All.SendAsync("PlanLoaded", _plans.SharedPlan);
    }

    public async Task LoadPlanFromFile(string filePath)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        try
        {
            var plan = await _plans.LoadAsync(filePath);
            _plans.SetSharedPlan(plan);
            await Clients.All.SendAsync("PlanLoaded", _plans.SharedPlan);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("Error", "Load plan failed: " + ex.Message);
        }
    }

    public async Task SavePlanToFile(TestPlan plan, string filePath)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        try
        {
            await _plans.SaveAsync(plan, filePath);
            _plans.SetSharedPlan(plan);
            await Clients.Caller.SendAsync("PlanSaved", filePath);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("Error", "Save plan failed: " + ex.Message);
        }
    }

    // ── Test execution ────────────────────────────────────────────────────────

    public async Task StartHead(int headNum)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _runner.StartAsync(headNum);
    }

    public async Task StartAll()
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _runner.StartAllAsync();
    }

    public async Task StopHead(int headNum)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _runner.StopAsync(headNum);
    }

    public async Task StopAll()
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _runner.StopAllAsync();
    }

    public async Task RetestHead(int headNum, int fromStep)
    {
        if (!CanControl()) { await Clients.Caller.SendAsync("Error", "No control access"); return; }
        await _runner.StartAsync(headNum, fromStep);
    }

    // ── Modules ───────────────────────────────────────────────────────────────

    public async Task GetModules() =>
        await Clients.Caller.SendAsync("ModulesList", _modules.GetAll());

    public async Task ReloadModules()
    {
        if (!IsAdmin()) { await Clients.Caller.SendAsync("Error", "Not authorized"); return; }
        await _modules.ScanAndLoadAsync();
        await Clients.Caller.SendAsync("ModulesList", _modules.GetAll());
    }

    public async Task LoadModuleFile(string filePath)
    {
        if (!IsAdmin()) { await Clients.Caller.SendAsync("Error", "Not authorized"); return; }
        var info = await _modules.LoadFileAsync(filePath);
        await Clients.Caller.SendAsync("ModuleLoaded", info);
        await Clients.Caller.SendAsync("ModulesList", _modules.GetAll());
    }

    // ── Admin sessions ────────────────────────────────────────────────────────

    public async Task GetSessions()
    {
        if (!IsAdmin()) return;
        await PushSessionList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task PushSessionList()
    {
        var payload = new
        {
            Sessions = _access.GetSessions(),
            PendingRequests = _access.GetPendingRequests(),
            ControllerId = _access.ControllerConnectionId
        };
        await Clients.Group(GroupAdmins).SendAsync("SessionList", payload);
    }

    private bool CanControl() =>
        _access.HasControl(Context.ConnectionId) || IsAdmin();

    private bool IsAdmin() =>
        _cfg.IsAdminIp(GetClientIp());

    private string GetClientIp()
    {
        var ctx = Context.GetHttpContext();
        if (ctx == null) return "unknown";
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fwd)) return fwd.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
