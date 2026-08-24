// VISA instrument control via visa32.dll (NI-VISA / Keysight IO Libraries must be installed).
// All SCPI functions write the instrument response to ctx.MeasureValue.
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;
using System.Runtime.InteropServices;
using System.Text;

[FMainModule("VISA Module", "SCPI instrument control via VISA (NI-VISA or Keysight IOLS required)", "VISA", "1.0.0")]
public class VisaModule
{
    // ── visa32.dll P/Invoke ───────────────────────────────────────────────────

    private const int VI_SUCCESS      = 0;
    private const int VI_NO_LOCK      = 0;
    private const int VI_ATTR_TMO_VALUE  = unchecked((int)0x3FFF001A);
    private const int VI_ATTR_TERMCHAR_EN = unchecked((int)0x3FFF0038);
    private const int VI_ATTR_TERMCHAR    = unchecked((int)0x3FFF0018);

    [DllImport("visa32.dll")] static extern int viOpenDefaultRM(out int vi);
    [DllImport("visa32.dll")] static extern int viOpen(int sesn, [MarshalAs(UnmanagedType.LPStr)] string rsrc, int mode, int to, out int vi);
    [DllImport("visa32.dll")] static extern int viClose(int vi);
    [DllImport("visa32.dll")] static extern int viSetAttribute(int vi, int attr, int val);
    [DllImport("visa32.dll")] static extern int viWrite(int vi, [MarshalAs(UnmanagedType.LPArray)] byte[] buf, int cnt, out int ret);
    [DllImport("visa32.dll")] static extern int viRead(int vi, [MarshalAs(UnmanagedType.LPArray)] byte[] buf, int cnt, out int ret);
    [DllImport("visa32.dll")] static extern int viFindRsrc(int vi, [MarshalAs(UnmanagedType.LPStr)] string expr, out int fl, out int cnt, StringBuilder desc);
    [DllImport("visa32.dll")] static extern int viFindNext(int fl, StringBuilder desc);

    // ── Session state ─────────────────────────────────────────────────────────

    // Key = resource name string, Value = (rmSession, instrSession)
    private static readonly Dictionary<string, (int rm, int vi)> _sess = new();
    private static readonly object _lock = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryGetSession(string resource, out int vi)
    {
        vi = 0;
        lock (_lock)
        {
            if (_sess.TryGetValue(resource.Trim(), out var s)) { vi = s.vi; return true; }
            return false;
        }
    }

    private static void Send(int vi, string cmd)
    {
        var b = Encoding.ASCII.GetBytes(cmd + "\n");
        viWrite(vi, b, b.Length, out _);
    }

    private static string Query(int vi, string cmd)
    {
        Send(vi, cmd);
        var buf = new byte[4096];
        int st = viRead(vi, buf, buf.Length, out int ret);
        if (st < VI_SUCCESS || ret == 0) return $"Error:0x{st:X}";
        return Encoding.ASCII.GetString(buf, 0, ret).TrimEnd('\n', '\r', ' ');
    }

    private static void Fail(TestContext? ctx, string msg)
    {
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = false; }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    [FMainFunction("Connect to a VISA instrument (e.g. USB0::0x2A8D::0x1301::MY12345::INSTR)", "VISA")]
    public void visa_connect(string resourceName, string timeoutMs = "5000")
    {
        var ctx = TestContext.Current.Value;
        var key = resourceName.Trim();
        lock (_lock)
        {
            if (_sess.ContainsKey(key)) { if (ctx != null) ctx.MeasureValue = "Already connected"; return; }
            try
            {
                int st = viOpenDefaultRM(out int rm);
                if (st < VI_SUCCESS) { Fail(ctx, $"viOpenDefaultRM: 0x{st:X}"); return; }
                st = viOpen(rm, key, VI_NO_LOCK, 2000, out int vi);
                if (st < VI_SUCCESS) { viClose(rm); Fail(ctx, $"viOpen: 0x{st:X}"); return; }
                viSetAttribute(vi, VI_ATTR_TMO_VALUE,   int.Parse(timeoutMs));
                viSetAttribute(vi, VI_ATTR_TERMCHAR_EN, 1);
                viSetAttribute(vi, VI_ATTR_TERMCHAR,    '\n');
                _sess[key] = (rm, vi);
                if (ctx != null) ctx.MeasureValue = "Connected";
            }
            catch (Exception ex) { Fail(ctx, ex.Message); }
        }
    }

    [FMainFunction("Disconnect from a VISA instrument", "VISA")]
    public void visa_disconnect(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        var key = resourceName.Trim();
        lock (_lock)
        {
            if (_sess.TryGetValue(key, out var s))
            {
                try { viClose(s.vi); viClose(s.rm); } catch { }
                _sess.Remove(key);
            }
        }
        if (ctx != null) ctx.MeasureValue = "Disconnected";
    }

    [FMainFunction("List available VISA resources (Measure = semicolon-delimited list)", "VISA")]
    public void visa_list_resources(string filter = "?*INSTR")
    {
        var ctx = TestContext.Current.Value;
        try
        {
            if (viOpenDefaultRM(out int rm) < VI_SUCCESS) { Fail(ctx, "viOpenDefaultRM failed"); return; }
            var desc = new StringBuilder(256);
            int st = viFindRsrc(rm, filter, out int fl, out int cnt, desc);
            if (st < VI_SUCCESS || cnt == 0)
            {
                viClose(rm);
                if (ctx != null) ctx.MeasureValue = "NONE";
                return;
            }
            var list = new System.Collections.Generic.List<string>();
            list.Add(desc.ToString());
            for (int i = 1; i < cnt; i++) { viFindNext(fl, desc); list.Add(desc.ToString()); }
            viClose(fl);
            viClose(rm);
            if (ctx != null) ctx.MeasureValue = string.Join("; ", list);
        }
        catch (Exception ex) { Fail(ctx, ex.Message); }
    }

    // ── Generic SCPI ──────────────────────────────────────────────────────────

    [FMainFunction("Send a SCPI command (no response expected)", "VISA")]
    public void visa_send(string resourceName, string command)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, command);
        if (ctx != null) ctx.MeasureValue = "SENT";
    }

    [FMainFunction("Send a SCPI query; writes response to Measure column", "VISA")]
    public void visa_query(string resourceName, string command)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, command);
    }

    [FMainFunction("Send *IDN? and write response to Measure column", "VISA")]
    public void visa_idn(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, "*IDN?");
    }

    // ── U3606B / DMM configuration ────────────────────────────────────────────

    [FMainFunction("Config DMM: DC voltage range in V (0.02 / 0.1 / 1 / 10 / 100 / 1000)", "VISA")]
    public void dmm_config_dcvolt(string resourceName, string rangeV = "10")
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $"CONF:VOLT:DC {rangeV}");
        if (ctx != null) ctx.MeasureValue = $"DCVOLT:{rangeV}V";
    }

    [FMainFunction("Config DMM: DC current range in A (0.01 / 0.1 / 1 / 3)", "VISA")]
    public void dmm_config_dccurr(string resourceName, string rangeA = "1")
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $"CONF:CURR:DC {rangeA}");
        if (ctx != null) ctx.MeasureValue = $"DCCURR:{rangeA}A";
    }

    [FMainFunction("Config DMM: resistance range (100 / 1K / 10K / 100K / 1M / 10M / 100M)", "VISA")]
    public void dmm_config_resistance(string resourceName, string range = "10K")
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $"CONF:RES {range}");
        if (ctx != null) ctx.MeasureValue = $"RES:{range}";
    }

    [FMainFunction("Config DMM: AC voltage range in V (0.1 / 1 / 10 / 100 / 750)", "VISA")]
    public void dmm_config_acvolt(string resourceName, string rangeV = "10")
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $"CONF:VOLT:AC {rangeV}");
        if (ctx != null) ctx.MeasureValue = $"ACVOLT:{rangeV}V";
    }

    [FMainFunction("Config DMM: AC current range in A (0.01 / 0.1 / 1 / 3)", "VISA")]
    public void dmm_config_accurr(string resourceName, string rangeA = "1")
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $"CONF:CURR:AC {rangeA}");
        if (ctx != null) ctx.MeasureValue = $"ACCURR:{rangeA}A";
    }

    [FMainFunction("Config DMM: frequency measurement", "VISA")]
    public void dmm_config_frequency(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, "CONF:FREQ");
        if (ctx != null) ctx.MeasureValue = "FREQ";
    }

    // ── U3606B / DMM measurement ──────────────────────────────────────────────

    [FMainFunction("Measure DC voltage; writes result to Measure column", "VISA")]
    public void dmm_read_dcvolt(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":MEAS:VOLT:DC?");
    }

    [FMainFunction("Measure DC current; writes result to Measure column", "VISA")]
    public void dmm_read_dccurr(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, "MEASure:CURRent:DC?");
    }

    [FMainFunction("Measure resistance; writes result to Measure column", "VISA")]
    public void dmm_read_resistance(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":MEAS:RES?");
    }

    [FMainFunction("Measure AC voltage; writes result to Measure column", "VISA")]
    public void dmm_read_acvolt(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":MEAS:VOLT:AC?");
    }

    [FMainFunction("Measure AC current; writes result to Measure column", "VISA")]
    public void dmm_read_accurr(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, "READ?");
    }

    [FMainFunction("Measure frequency; writes result to Measure column", "VISA")]
    public void dmm_read_frequency(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":MEAS:FREQ?");
    }

    // ── U3606B source / PSU mode ──────────────────────────────────────────────

    [FMainFunction("Enable U3606B source output", "VISA")]
    public void dmm_source_on(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, ":OUTP:STAT ON");
        if (ctx != null) ctx.MeasureValue = "SOURCE_ON";
    }

    [FMainFunction("Disable U3606B source output", "VISA")]
    public void dmm_source_off(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, ":OUTP:STAT OFF");
        if (ctx != null) ctx.MeasureValue = "SOURCE_OFF";
    }

    [FMainFunction("Set U3606B source voltage (volts)", "VISA")]
    public void dmm_source_voltage(string resourceName, string voltageV)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        Send(vi, $":SOUR:VOLT {voltageV}");
        if (ctx != null) ctx.MeasureValue = $"{voltageV}V_SET";
    }

    [FMainFunction("Read U3606B output voltage sense", "VISA")]
    public void dmm_source_read_voltage(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":SOUR:SENS:VOLT?");
    }

    [FMainFunction("Read U3606B output current sense", "VISA")]
    public void dmm_source_read_current(string resourceName)
    {
        var ctx = TestContext.Current.Value;
        if (!TryGetSession(resourceName, out int vi)) { Fail(ctx, $"Not connected: {resourceName}"); return; }
        if (ctx != null) ctx.MeasureValue = Query(vi, ":SOUR:SENS:CURR?");
    }
}
