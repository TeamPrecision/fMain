// USB HID barcode scanner module (keyboard-emulation mode).
// Delegates to BarcodeService (Windows low-level keyboard hook).
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;

[FMainModule("Barcode Module", "USB HID barcode/QR scanner input (keyboard emulation, server-side)", "Barcode", "1.0.0")]
public class BarcodeModule
{
    [FMainFunction("Wait for scanner to scan a code; writes barcode string to Measure column", "Barcode")]
    public void barcode_read(string timeoutMs = "10000")
    {
        var ctx = TestContext.Current.Value;
        var svc = BarcodeService.Instance;
        if (svc == null) { Fail(ctx, "BarcodeService not running"); return; }

        var ct = ctx?.CancellationToken ?? System.Threading.CancellationToken.None;
        using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(int.Parse(timeoutMs));
        try
        {
            var code = svc.WaitForScanAsync(cts.Token).GetAwaiter().GetResult();
            if (ctx != null) ctx.MeasureValue = code;
        }
        catch (System.OperationCanceledException)
        {
            Fail(ctx, "TIMEOUT");
        }
    }

    [FMainFunction("Scan barcode and validate optional prefix and/or fixed length; PASS=valid, FAIL=invalid", "Barcode")]
    public void barcode_validate(string timeoutMs = "10000", string prefix = "", string length = "0")
    {
        var ctx = TestContext.Current.Value;
        var svc = BarcodeService.Instance;
        if (svc == null) { Fail(ctx, "BarcodeService not running"); return; }

        var ct = ctx?.CancellationToken ?? System.Threading.CancellationToken.None;
        using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(int.Parse(timeoutMs));
        try
        {
            var code = svc.WaitForScanAsync(cts.Token).GetAwaiter().GetResult();
            if (ctx != null) ctx.MeasureValue = code;

            bool ok = true;
            if (!string.IsNullOrEmpty(prefix) && !code.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                ok = false;
            int len = int.Parse(length);
            if (len > 0 && code.Length != len)
                ok = false;

            if (ctx != null) ctx.ForceResult = ok;
        }
        catch (System.OperationCanceledException)
        {
            Fail(ctx, "TIMEOUT");
        }
    }

    private static void Fail(TestContext? ctx, string msg)
    {
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = false; }
    }
}
