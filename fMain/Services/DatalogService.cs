using fMain.Models;
using MySqlConnector;

namespace fMain.Services;

public class DatalogService
{
    private readonly ConfigService _cfg;
    private readonly ILogger<DatalogService> _logger;

    public DatalogService(ConfigService cfg, ILogger<DatalogService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync()
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS test_log (
                    id            BIGINT AUTO_INCREMENT PRIMARY KEY,
                    work_order    VARCHAR(64),
                    serial_number VARCHAR(64),
                    head          INT,
                    start_time    DATETIME,
                    end_time      DATETIME,
                    result        VARCHAR(8),
                    plan_name     VARCHAR(128),
                    plan_version  VARCHAR(32),
                    INDEX idx_sn (serial_number),
                    INDEX idx_wo (work_order),
                    INDEX idx_start (start_time)
                );
                CREATE TABLE IF NOT EXISTS test_step (
                    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
                    log_id      BIGINT,
                    step_num    INT,
                    description VARCHAR(256),
                    function    VARCHAR(128),
                    measure     VARCHAR(256),
                    result      VARCHAR(8),
                    limit_min   VARCHAR(64),
                    limit_max   VARCHAR(64),
                    unit        VARCHAR(32),
                    INDEX idx_log (log_id)
                );";
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("DatalogService: schema ready");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DatalogService: schema creation failed (MySQL may not be configured)");
        }
    }

    public async Task<long> SaveAsync(HeadState state, TestPlan plan, DateTime endTime)
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return 0;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var logId = await InsertLogAsync(conn, tx, state, plan, endTime);

            int stepNum = 0;
            foreach (var s in state.Steps)
            {
                if (string.Equals(s.RowType, "Header", StringComparison.OrdinalIgnoreCase)) continue;
                stepNum++;
                ParseLimit(s.Limit, out var lmin, out var lmax, out var unit);
                await using var sc = conn.CreateCommand();
                sc.Transaction = tx;
                sc.CommandText = @"INSERT INTO test_step
                    (log_id, step_num, description, function, measure, result, limit_min, limit_max, unit)
                    VALUES (@lid,@sn,@desc,@fn,@meas,@res,@lmin,@lmax,@unit)";
                sc.Parameters.AddWithValue("@lid",  logId);
                sc.Parameters.AddWithValue("@sn",   stepNum);
                sc.Parameters.AddWithValue("@desc", s.Step);
                sc.Parameters.AddWithValue("@fn",   s.Function);
                sc.Parameters.AddWithValue("@meas", s.Measure);
                sc.Parameters.AddWithValue("@res",  s.Result);
                sc.Parameters.AddWithValue("@lmin", lmin);
                sc.Parameters.AddWithValue("@lmax", lmax);
                sc.Parameters.AddWithValue("@unit", unit);
                await sc.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _logger.LogInformation("DatalogService: saved log {Id} for head {Head} SN={SN}", logId, state.HeadNumber, state.SerialNumber);
            return logId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DatalogService.SaveAsync failed (head {Head})", state.HeadNumber);
            return 0;
        }
    }

    public async Task<object> QueryAsync(string? sn, string? wo, DateTime? from, DateTime? to, int limit = 100)
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            return new { error = "MySQL not configured" };
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();

            var where = new List<string>();
            if (!string.IsNullOrEmpty(sn))  { where.Add("serial_number=@sn");   cmd.Parameters.AddWithValue("@sn", sn); }
            if (!string.IsNullOrEmpty(wo))  { where.Add("work_order=@wo");       cmd.Parameters.AddWithValue("@wo", wo); }
            if (from.HasValue)              { where.Add("start_time>=@from");     cmd.Parameters.AddWithValue("@from", from.Value); }
            if (to.HasValue)                { where.Add("start_time<=@to");       cmd.Parameters.AddWithValue("@to", to.Value); }

            var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"SELECT * FROM test_log {whereClause} ORDER BY id DESC LIMIT {Math.Min(limit, 500)}";

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    public async Task<object> QueryStepsAsync(long logId)
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return new { error = "MySQL not configured" };
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM test_step WHERE log_id=@id ORDER BY step_num";
            cmd.Parameters.AddWithValue("@id", logId);
            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    public async Task<object> QueryStatsAsync(string? wo, string? stepDesc, DateTime? from, DateTime? to)
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return new { error = "MySQL not configured" };
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();

            var where = new List<string>();
            if (!string.IsNullOrEmpty(wo))       { where.Add("l.work_order=@wo");       cmd.Parameters.AddWithValue("@wo", wo); }
            if (!string.IsNullOrEmpty(stepDesc)) { where.Add("s.description LIKE @sd"); cmd.Parameters.AddWithValue("@sd", $"%{stepDesc}%"); }
            if (from.HasValue)                   { where.Add("l.start_time>=@from");     cmd.Parameters.AddWithValue("@from", from.Value); }
            if (to.HasValue)                     { where.Add("l.start_time<=@to");       cmd.Parameters.AddWithValue("@to", to.Value); }

            var wc = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $@"
                SELECT s.description, s.function, s.unit,
                    MIN(s.limit_min) as lmin, MIN(s.limit_max) as lmax,
                    COUNT(*) as total,
                    SUM(CASE WHEN s.result='PASS' THEN 1 ELSE 0 END) as pass_count,
                    SUM(CASE WHEN s.result='FAIL' THEN 1 ELSE 0 END) as fail_count,
                    AVG(CAST(NULLIF(s.measure,'') AS DECIMAL(20,6))) as avg_val,
                    STDDEV_POP(CAST(NULLIF(s.measure,'') AS DECIMAL(20,6))) as stddev_val,
                    MIN(CAST(NULLIF(s.measure,'') AS DECIMAL(20,6))) as min_val,
                    MAX(CAST(NULLIF(s.measure,'') AS DECIMAL(20,6))) as max_val
                FROM test_step s JOIN test_log l ON s.log_id = l.id
                {wc}
                GROUP BY s.description, s.function, s.unit
                ORDER BY s.description";

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long total = reader.GetInt64(reader.GetOrdinal("total"));
                long pass = reader.GetInt64(reader.GetOrdinal("pass_count"));
                double? avg = reader.IsDBNull(reader.GetOrdinal("avg_val")) ? null : reader.GetDouble(reader.GetOrdinal("avg_val"));
                double? sd  = reader.IsDBNull(reader.GetOrdinal("stddev_val")) ? null : reader.GetDouble(reader.GetOrdinal("stddev_val"));
                string lmin = reader.IsDBNull(reader.GetOrdinal("lmin")) ? "" : reader.GetString(reader.GetOrdinal("lmin"));
                string lmax = reader.IsDBNull(reader.GetOrdinal("lmax")) ? "" : reader.GetString(reader.GetOrdinal("lmax"));
                double? minV = reader.IsDBNull(reader.GetOrdinal("min_val")) ? null : reader.GetDouble(reader.GetOrdinal("min_val"));
                double? maxV = reader.IsDBNull(reader.GetOrdinal("max_val")) ? null : reader.GetDouble(reader.GetOrdinal("max_val"));

                double? cp = null, cpk = null;
                if (avg.HasValue && sd.HasValue && sd > 0
                    && double.TryParse(lmin, out var lo) && double.TryParse(lmax, out var hi))
                {
                    cp  = (hi - lo) / (6 * sd.Value);
                    cpk = Math.Min((hi - avg.Value) / (3 * sd.Value), (avg.Value - lo) / (3 * sd.Value));
                }

                rows.Add(new {
                    description = reader.GetString(reader.GetOrdinal("description")),
                    function    = reader.GetString(reader.GetOrdinal("function")),
                    unit        = reader.IsDBNull(reader.GetOrdinal("unit")) ? "" : reader.GetString(reader.GetOrdinal("unit")),
                    lmin, lmax, total, pass,
                    fail        = total - pass,
                    yield       = total > 0 ? Math.Round(pass * 100.0 / total, 1) : 0,
                    avg = avg.HasValue ? Math.Round(avg.Value, 4) : (double?)null,
                    stddev = sd.HasValue ? Math.Round(sd.Value, 6) : (double?)null,
                    min = minV.HasValue ? Math.Round(minV.Value, 4) : (double?)null,
                    max = maxV.HasValue ? Math.Round(maxV.Value, 4) : (double?)null,
                    cp  = cp.HasValue  ? Math.Round(cp.Value,  3) : (double?)null,
                    cpk = cpk.HasValue ? Math.Round(cpk.Value, 3) : (double?)null
                });
            }
            return rows;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    public async Task<object> QueryTrendAsync(string description, string? wo, DateTime? from, DateTime? to, int limit = 200)
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return new { error = "MySQL not configured" };
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();

            var where = new List<string> { "s.description=@desc" };
            cmd.Parameters.AddWithValue("@desc", description);
            if (!string.IsNullOrEmpty(wo)) { where.Add("l.work_order=@wo"); cmd.Parameters.AddWithValue("@wo", wo); }
            if (from.HasValue) { where.Add("l.start_time>=@from"); cmd.Parameters.AddWithValue("@from", from.Value); }
            if (to.HasValue)   { where.Add("l.start_time<=@to");   cmd.Parameters.AddWithValue("@to",   to.Value); }

            cmd.CommandText = $@"
                SELECT l.start_time, l.serial_number, s.measure, s.result, s.limit_min, s.limit_max, s.unit
                FROM test_step s JOIN test_log l ON s.log_id = l.id
                WHERE {string.Join(" AND ", where)}
                ORDER BY l.start_time DESC
                LIMIT {Math.Min(limit, 1000)}";

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int k = 0; k < reader.FieldCount; k++)
                    row[reader.GetName(k)] = reader.IsDBNull(k) ? null : reader.GetValue(k);
                rows.Add(row);
            }
            rows.Reverse();
            return rows;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        var cs = _cfg.Config.MySql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return false;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            return true;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<long> InsertLogAsync(
        MySqlConnection conn, MySqlTransaction tx,
        HeadState state, TestPlan plan, DateTime endTime)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO test_log
            (work_order,serial_number,head,start_time,end_time,result,plan_name,plan_version)
            VALUES (@wo,@sn,@head,@st,@et,@res,@pn,@pv);
            SELECT LAST_INSERT_ID();";
        cmd.Parameters.AddWithValue("@wo",   state.WorkOrder ?? "");
        cmd.Parameters.AddWithValue("@sn",   state.SerialNumber ?? "");
        cmd.Parameters.AddWithValue("@head", state.HeadNumber);
        cmd.Parameters.AddWithValue("@st",   state.StartTime ?? DateTime.Now);
        cmd.Parameters.AddWithValue("@et",   endTime);
        cmd.Parameters.AddWithValue("@res",  state.Status == HeadStatus.Pass ? "PASS" : "FAIL");
        cmd.Parameters.AddWithValue("@pn",   plan.Name);
        cmd.Parameters.AddWithValue("@pv",   plan.Version);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // Reverse the FormatLimit output: "min~max unit" | "≤max unit" | "≥min unit"
    private static void ParseLimit(string limit, out string lmin, out string lmax, out string unit)
    {
        lmin = ""; lmax = ""; unit = "";
        if (string.IsNullOrEmpty(limit)) return;
        var s = limit.Trim();
        var lastSpace = s.LastIndexOf(' ');
        if (lastSpace >= 0) { unit = s[(lastSpace + 1)..]; s = s[..lastSpace]; }
        if (s.Contains('~'))
        {
            var p = s.Split('~', 2);
            lmin = p[0];
            lmax = p.Length > 1 ? p[1] : "";
        }
        else if (s.StartsWith('≤')) lmax = s[1..];
        else if (s.StartsWith('≥')) lmin = s[1..];
        else lmin = s;   // single value treated as min
    }
}
