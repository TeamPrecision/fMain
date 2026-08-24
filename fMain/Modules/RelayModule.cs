// N4D3E16 RS485 Modbus relay card — 16 DI / 16 DO
// Protocol: FC06 (write single register) for DO; FC03 (read holding register) for DI/DO state.
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;
using System.IO.Ports;

[FMainModule("Relay Module", "N4D3E16 RS485 Modbus relay card control (16 DI / 16 DO)", "Relay", "1.0.0")]
public class RelayModule
{
    // Shared across all invocations (static persists for the lifetime of the compiled assembly)
    private static readonly Dictionary<string, SerialPort> _ports = new();
    private static readonly object _lock = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ushort Crc16(byte[] data, int len)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < len; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = ((crc & 1) != 0) ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    // FC06 write-single-register packet: {id, 0x06, regHi, regLo, dataHi, dataLo, CRC_L, CRC_H}
    private static byte[] Pkt(byte id, byte fc, byte rH, byte rL, byte dH, byte dL)
    {
        var p = new byte[] { id, fc, rH, rL, dH, dL, 0, 0 };
        var c = Crc16(p, 6);
        p[6] = (byte)(c & 0xFF);
        p[7] = (byte)(c >> 8);
        return p;
    }

    private static SerialPort? GetPort(string comPort)
    {
        lock (_lock)
        {
            var key = comPort.ToUpperInvariant();
            return _ports.TryGetValue(key, out var p) && p.IsOpen ? p : null;
        }
    }

    private static bool WaitBytes(SerialPort port, int count, int timeoutMs = 500)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (port.BytesToRead < count && DateTime.UtcNow < end)
            System.Threading.Thread.Sleep(10);
        return port.BytesToRead >= count;
    }

    private static void Fail(TestContext? ctx, string msg)
    {
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = false; }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    [FMainFunction("Connect to relay card on specified COM port (baud default 9600)", "Relay")]
    public void relay_connect(string comPort, string baud = "9600")
    {
        var ctx = TestContext.Current.Value;
        var key = comPort.ToUpperInvariant();
        lock (_lock)
        {
            if (_ports.TryGetValue(key, out var ex) && ex.IsOpen)
            {
                if (ctx != null) ctx.MeasureValue = $"Already open: {comPort}";
                return;
            }
            try
            {
                var p = new SerialPort(comPort, int.Parse(baud), Parity.None, 8, StopBits.One)
                    { ReadTimeout = 500, WriteTimeout = 2000 };
                p.Open();
                _ports[key] = p;
                if (ctx != null) ctx.MeasureValue = $"Connected: {comPort} @ {baud}";
            }
            catch (Exception ex2) { Fail(ctx, $"Error: {ex2.Message}"); }
        }
    }

    [FMainFunction("Disconnect relay card on specified COM port", "Relay")]
    public void relay_disconnect(string comPort)
    {
        var ctx = TestContext.Current.Value;
        var key = comPort.ToUpperInvariant();
        lock (_lock)
        {
            if (_ports.TryGetValue(key, out var p))
            {
                try { if (p.IsOpen) p.Close(); } catch { }
                _ports.Remove(key);
            }
        }
        if (ctx != null) ctx.MeasureValue = $"Disconnected: {comPort}";
    }

    // ── Output (DO) write ──────────────────────────────────────────────────────
    // Protocol: FC06, register = channel (1-16), data = {operation, 0}
    //   operation: 1=ON, 2=OFF, 3=Toggle, 4=Latch
    // Response: 8 bytes echo.

    [FMainFunction("Turn ON one relay output channel (channel 1–16)", "Relay")]
    public void relay_on(string comPort, string moduleId, string channel)
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        var cmd = Pkt(byte.Parse(moduleId), 6, 0, byte.Parse(channel), 1, 0);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        bool ok = WaitBytes(port, 8);
        if (ok) { var buf = new byte[8]; port.Read(buf, 0, 8); }
        if (ctx != null) { ctx.MeasureValue = ok ? "ON" : "TIMEOUT"; if (!ok) ctx.ForceResult = false; }
    }

    [FMainFunction("Turn OFF one relay output channel (channel 1–16)", "Relay")]
    public void relay_off(string comPort, string moduleId, string channel)
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        var cmd = Pkt(byte.Parse(moduleId), 6, 0, byte.Parse(channel), 2, 0);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        bool ok = WaitBytes(port, 8);
        if (ok) { var buf = new byte[8]; port.Read(buf, 0, 8); }
        if (ctx != null) { ctx.MeasureValue = ok ? "OFF" : "TIMEOUT"; if (!ok) ctx.ForceResult = false; }
    }

    [FMainFunction("Toggle one relay output channel", "Relay")]
    public void relay_toggle(string comPort, string moduleId, string channel)
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        var cmd = Pkt(byte.Parse(moduleId), 6, 0, byte.Parse(channel), 3, 0);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        bool ok = WaitBytes(port, 8);
        if (ok) { var buf = new byte[8]; port.Read(buf, 0, 8); }
        if (ctx != null) { ctx.MeasureValue = ok ? "TOGGLE" : "TIMEOUT"; if (!ok) ctx.ForceResult = false; }
    }

    [FMainFunction("Turn OFF all 16 outputs on the module (FC06 reg=0 val=0x0800)", "Relay")]
    public void relay_off_all(string comPort, string moduleId = "0")
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        // byte[4]=8 → dataHi=8, byte[5]=0 → dataLo=0 → register value 0x0800
        var cmd = Pkt(byte.Parse(moduleId), 6, 0, 0, 8, 0);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        if (WaitBytes(port, 8)) { var buf = new byte[8]; port.Read(buf, 0, 8); }
        if (ctx != null) ctx.MeasureValue = "ALL_OFF";
    }

    // ── Input (DI) read ───────────────────────────────────────────────────────
    // Protocol: FC03, register = 0x80 + channel → response[4] = 1 if ON
    // ReadAll:  FC03, register = 0xC0 → response[3]<<8 | response[4] = 16-bit bitmask

    [FMainFunction("Read state of one input channel (Measure = 0 or 1)", "Relay")]
    public void relay_read(string comPort, string moduleId, string channel)
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        byte reg = (byte)(0x80 + int.Parse(channel));
        var cmd = Pkt(byte.Parse(moduleId), 3, 0, reg, 0, 1);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        if (!WaitBytes(port, 7)) { Fail(ctx, "TIMEOUT"); return; }
        var resp = new byte[7]; port.Read(resp, 0, 7);
        if (resp[1] == 3 && ctx != null) ctx.MeasureValue = resp[4].ToString();
        else Fail(ctx, "BAD_RESPONSE");
    }

    [FMainFunction("Read all 16 input channels (Measure = 0xHHHH 16-bit bitmask)", "Relay")]
    public void relay_read_all(string comPort, string moduleId)
    {
        var ctx = TestContext.Current.Value;
        var port = GetPort(comPort);
        if (port == null) { Fail(ctx, $"Not connected: {comPort}"); return; }
        var cmd = Pkt(byte.Parse(moduleId), 3, 0, 0xC0, 0, 1);
        port.ReadExisting();
        port.Write(cmd, 0, cmd.Length);
        if (!WaitBytes(port, 7)) { Fail(ctx, "TIMEOUT"); return; }
        var resp = new byte[7]; port.Read(resp, 0, 7);
        if (resp[1] == 3 && ctx != null) ctx.MeasureValue = $"0x{(resp[3] << 8 | resp[4]):X4}";
        else Fail(ctx, "BAD_RESPONSE");
    }

    // ── Auto-scan ─────────────────────────────────────────────────────────────

    [FMainFunction("Scan COM port for relay modules (addr 0–maxAddr), Measure = comma-sep found IDs", "Relay")]
    public void relay_scan(string comPort, string baud = "9600", string maxAddr = "32")
    {
        var ctx = TestContext.Current.Value;
        int max = int.Parse(maxAddr);
        var key = comPort.ToUpperInvariant();
        SerialPort? port = null;
        bool owned = false;
        try
        {
            lock (_lock)
            {
                if (!_ports.TryGetValue(key, out port) || !port.IsOpen)
                {
                    port = new SerialPort(comPort, int.Parse(baud), Parity.None, 8, StopBits.One)
                        { ReadTimeout = 150, WriteTimeout = 500 };
                    port.Open();
                    owned = true;
                }
            }

            var found = new System.Collections.Generic.List<string>();
            for (int addr = 0; addr <= max; addr++)
            {
                var cmd = Pkt((byte)addr, 3, 0, 0xC0, 0, 1);
                try
                {
                    port.ReadExisting();
                    port.Write(cmd, 0, cmd.Length);
                    var end = DateTime.UtcNow.AddMilliseconds(150);
                    while (port.BytesToRead < 7 && DateTime.UtcNow < end)
                        System.Threading.Thread.Sleep(10);
                    if (port.BytesToRead >= 7)
                    {
                        var resp = new byte[7]; port.Read(resp, 0, 7);
                        if (resp[0] == (byte)addr && resp[1] == 3) found.Add(addr.ToString());
                    }
                }
                catch { }
            }
            if (ctx != null) ctx.MeasureValue = found.Count > 0 ? string.Join(",", found) : "NONE";
        }
        catch (Exception ex) { Fail(ctx, $"Error: {ex.Message}"); }
        finally { if (owned && port != null) try { port.Close(); } catch { } }
    }
}
