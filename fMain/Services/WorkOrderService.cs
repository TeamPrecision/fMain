using fMain.Models;

namespace fMain.Services;

public class WorkOrderService
{
    private readonly HeadStateService _heads;
    private readonly PrismService _prism;
    private readonly ILogger<WorkOrderService> _logger;

    private readonly Dictionary<int, WOEntry> _entries = new();
    private readonly object _lock = new();

    public WorkOrderService(HeadStateService heads, PrismService prism, ILogger<WorkOrderService> logger)
    {
        _heads = heads;
        _prism = prism;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task SetWorkOrderAsync(int head, string wo)
    {
        await _heads.SetWorkOrder(head, wo);

        lock (_lock)
        {
            if (string.IsNullOrEmpty(wo))
            {
                _entries.Remove(head);
                return;
            }
            if (!_entries.TryGetValue(head, out var entry) || entry.WorkOrder != wo)
                _entries[head] = new WOEntry { WorkOrder = wo };
        }

        // Fetch qty from Prism (may be a no-op in Debug mode)
        if (!string.IsNullOrEmpty(wo))
        {
            var arr = _prism.GetWorkOrderInfo(wo);
            if (arr != null && arr.Length > 4 && int.TryParse(arr[4], out var qty))
            {
                lock (_lock)
                {
                    if (_entries.TryGetValue(head, out var e)) e.Qty = qty;
                }
            }
        }
    }

    public async Task SetWorkOrderAllAsync(string wo)
    {
        foreach (var h in _heads.GetAll())
            await SetWorkOrderAsync(h.HeadNumber, wo);
    }

    public string? GetWorkOrder(int head) => _heads.Get(head)?.WorkOrder;

    public WOEntry? GetEntry(int head)
    {
        lock (_lock) { _entries.TryGetValue(head, out var e); return e; }
    }

    public IReadOnlyDictionary<int, WOEntry> GetAll()
    {
        lock (_lock) return new Dictionary<int, WOEntry>(_entries);
    }

    public void RecordTestComplete(int head, bool passed)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(head, out var e)) return;
            e.Tested++;
            if (passed) e.Passed++; else e.Failed++;
        }
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    public class WOEntry
    {
        public string WorkOrder { get; set; } = string.Empty;
        public int Qty     { get; set; }
        public int Tested  { get; set; }
        public int Passed  { get; set; }
        public int Failed  { get; set; }
        public int Remaining => Math.Max(0, Qty - Tested);
    }
}
