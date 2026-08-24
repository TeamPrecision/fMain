// Example built-in module — shows how to author an fMain plugin.
// Project-specific modules go in D:/svn/ProjectX/Module/*.cs

using fMain.Services;

[FMainModule("Example Module", "Built-in stubs showing available function signatures", "Example", "1.0.0")]
public class ExampleModule
{
    [FMainFunction("Delay for the specified milliseconds", "Timing")]
    public void delay_ms(string ms)
    {
        System.Threading.Thread.Sleep(int.Parse(ms));
    }

    [FMainFunction("Force the current step to PASS", "Control")]
    public void test_pass()
    {
        if (TestContext.Current.Value is { } ctx) ctx.ForceResult = true;
    }

    [FMainFunction("Force the current step to FAIL", "Control")]
    public void test_fail()
    {
        if (TestContext.Current.Value is { } ctx) ctx.ForceResult = false;
    }

    [FMainFunction("Write a custom value to the Measure column", "Control")]
    public void test_write(string value)
    {
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = value;
    }

    [FMainFunction("Skip this step and mark PASS", "Control")]
    public void skip_pass()
    {
        if (TestContext.Current.Value is { } ctx) { ctx.ForceResult = true; ctx.MeasureValue = "SKIP"; }
    }

    [FMainFunction("Measure resistance and compare to min/max limits", "Measurement")]
    public void MeasureResistance(string min, string max, string range = "5000", string timeout = "1000")
    {
        // Stub: real implementation reads from DMM via VISA
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "9999.0";
    }

    [FMainFunction("Measure DC voltage and compare to limits", "Measurement")]
    public void MeasureVoltage(string min, string max, string timeout = "1000")
    {
        // Stub: real implementation reads from DMM via VISA
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "5.001";
    }

    [FMainFunction("Measure DC current and compare to limits", "Measurement")]
    public void MeasureCurrent(string min, string max, string timeout = "1000")
    {
        // Stub: real implementation reads from DMM via VISA
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "0.150";
    }

    [FMainFunction("Read a 2D barcode via camera or scanner", "Barcode")]
    public void Read2DBarCode()
    {
        // Stub: real implementation triggers camera capture
        if (TestContext.Current.Value is { } ctx) ctx.MeasureValue = "SN123456";
    }

    [FMainFunction("Validate serial number against Prism MES", "Prism")]
    public void PrismCheckProcess()
    {
        // Stub: real implementation calls Prism DLL
        if (TestContext.Current.Value is { } ctx) ctx.ForceResult = true;
    }
}
