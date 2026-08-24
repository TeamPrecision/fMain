// Demo module — software-only stubs for running demo test plans without real hardware.
// Provides MockMeasure (returns a fixed value), CheckString (compares strings),
// and relay/VISA stubs so demo plans execute end-to-end.
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;

[FMainModule("Demo Module", "Software-only stubs for demo plans — no hardware required", "Demo", "1.0.0")]
public class DemoModule
{
    [FMainFunction("Return a fixed numeric value (simulates a hardware measurement)", "Demo")]
    public void MockMeasure(string value)
    {
        if (TestContext.Current.Value is { } ctx)
            ctx.MeasureValue = value;
    }

    [FMainFunction("Compare two strings; PASS if equal, FAIL if not", "Demo")]
    public void CheckString(string expected, string actual)
    {
        var ctx = TestContext.Current.Value;
        if (ctx == null) return;
        bool ok = string.Equals(expected.Trim(), actual.Trim(), System.StringComparison.OrdinalIgnoreCase);
        ctx.MeasureValue = actual;
        ctx.ForceResult = ok;
    }

    [FMainFunction("Simulate opening all relays (no-op stub)", "Demo")]
    public void RelayOpenAll(string comPort = "DEMO")
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "ALL_OPEN(demo)";
    }

    [FMainFunction("Simulate closing a relay channel (no-op stub)", "Demo")]
    public void RelayClose(string channel, string comPort = "DEMO")
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = $"CLOSE_{channel}(demo)";
    }

    [FMainFunction("Simulate connecting to a VISA instrument (no-op stub)", "Demo")]
    public void VisaConnect(string resourceName)
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = $"CONNECTED(demo):{resourceName}";
    }

    [FMainFunction("Simulate sending a SCPI command (no-op stub)", "Demo")]
    public void VisaWrite(string resourceName, string command)
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "SENT(demo)";
    }

    [FMainFunction("Simulate a SCPI query; returns a fixed demo value", "Demo")]
    public void VisaQuery(string resourceName, string command, string demoValue = "5.001")
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = demoValue;
    }

    [FMainFunction("Simulate disconnecting from a VISA instrument (no-op stub)", "Demo")]
    public void VisaDisconnect(string resourceName)
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "DISCONNECTED(demo)";
    }
}
