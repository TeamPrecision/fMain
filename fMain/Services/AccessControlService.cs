using fMain.Models;

namespace fMain.Services;

public class AccessControlService
{
    private readonly Dictionary<string, ClientSession> _sessions = new();
    private readonly List<ControlRequest> _pending = new();
    private string? _controllerConnectionId;
    private readonly object _lock = new();
    private readonly ConfigService _cfg;
    private readonly ILogger<AccessControlService> _logger;

    public AccessControlService(ConfigService cfg, ILogger<AccessControlService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    public ClientSession Register(string connId, string ip, string name)
    {
        lock (_lock)
        {
            var session = new ClientSession
            {
                ConnectionId = connId,
                IpAddress = ip,
                DisplayName = name,
                IsAdmin = _cfg.IsAdminIp(ip),
                IsSpecialIp = _cfg.IsSpecialIp(ip),
                State = SessionState.Monitor
            };
            _sessions[connId] = session;
            _logger.LogInformation("Registered [{Name}] {IP} admin={A} special={S}", name, ip, session.IsAdmin, session.IsSpecialIp);
            return session;
        }
    }

    public void Remove(string connId)
    {
        lock (_lock)
        {
            _sessions.Remove(connId);
            if (_controllerConnectionId == connId)
                _controllerConnectionId = null;
            _pending.RemoveAll(r => r.ConnectionId == connId);
        }
    }

    // ── Control requests ──────────────────────────────────────────────────────

    /// <returns>(granted, requestId, message)</returns>
    public (bool Granted, string? RequestId, string Message) RequestControl(string connId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(connId, out var session))
                return (false, null, "Session not found");

            if (_controllerConnectionId == connId)
                return (true, null, "Already in control");

            // Special IP and server-localhost get immediate control
            if (session.IsSpecialIp || session.IsAdmin)
            {
                Grant(connId);
                return (true, null, "Granted immediately");
            }

            if (_controllerConnectionId != null)
                return (false, null, "Another session is in control");

            if (_pending.Any(r => r.ConnectionId == connId && r.Status == ControlRequestStatus.Pending))
                return (false, null, "Request already pending");

            var req = new ControlRequest
            {
                ConnectionId = connId,
                IpAddress = session.IpAddress,
                DisplayName = session.DisplayName
            };
            _pending.Add(req);
            session.State = SessionState.PendingControl;

            _logger.LogInformation("Control requested by [{Name}] {IP}", session.DisplayName, session.IpAddress);
            return (false, req.RequestId, "Pending approval");
        }
    }

    public (bool Ok, string? GrantedConnId) Approve(string requestId)
    {
        lock (_lock)
        {
            var req = _pending.FirstOrDefault(r => r.RequestId == requestId && r.Status == ControlRequestStatus.Pending);
            if (req == null) return (false, null);
            req.Status = ControlRequestStatus.Approved;
            Grant(req.ConnectionId);
            return (true, req.ConnectionId);
        }
    }

    public (bool Ok, string? DeniedConnId) Deny(string requestId)
    {
        lock (_lock)
        {
            var req = _pending.FirstOrDefault(r => r.RequestId == requestId && r.Status == ControlRequestStatus.Pending);
            if (req == null) return (false, null);
            req.Status = ControlRequestStatus.Denied;
            if (_sessions.TryGetValue(req.ConnectionId, out var s))
                s.State = SessionState.Monitor;
            return (true, req.ConnectionId);
        }
    }

    public bool Release(string connId)
    {
        lock (_lock)
        {
            if (_controllerConnectionId != connId) return false;
            _controllerConnectionId = null;
            if (_sessions.TryGetValue(connId, out var s))
                s.State = SessionState.Monitor;
            _logger.LogInformation("Control released by {ConnId}", connId);
            return true;
        }
    }

    private void Grant(string connId)
    {
        // Demote previous controller if any
        if (_controllerConnectionId != null && _sessions.TryGetValue(_controllerConnectionId, out var prev))
            prev.State = SessionState.Monitor;

        _controllerConnectionId = connId;
        if (_sessions.TryGetValue(connId, out var cur))
            cur.State = SessionState.Control;
        _logger.LogInformation("Control granted to {ConnId}", connId);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public string? ControllerConnectionId { get { lock (_lock) return _controllerConnectionId; } }

    public bool HasControl(string connId) { lock (_lock) return _controllerConnectionId == connId; }

    public ClientSession? GetSession(string connId) { lock (_lock) { _sessions.TryGetValue(connId, out var s); return s; } }

    public List<SessionInfo> GetSessions()
    {
        lock (_lock)
            return _sessions.Values.Select(s => new SessionInfo
            {
                SessionId = s.Id,
                DisplayName = s.DisplayName,
                IpAddress = s.IpAddress,
                State = s.State.ToString(),
                IsAdmin = s.IsAdmin,
                IsSpecialIp = s.IsSpecialIp,
                ConnectedAt = s.ConnectedAt
            }).ToList();
    }

    public List<ControlRequest> GetPendingRequests()
    {
        lock (_lock)
            return _pending.Where(r => r.Status == ControlRequestStatus.Pending).ToList();
    }
}
