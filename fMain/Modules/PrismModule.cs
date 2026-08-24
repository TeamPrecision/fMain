// TeamPrecision PRISM MES integration module.
// Wraps PrismService for use in test plans.
// This file is Roslyn-compiled at runtime; do NOT add it to the csproj.

using fMain.Services;

[FMainModule("Prism Module", "TeamPrecision PRISM MES integration (SN validation, result upload, WO query)", "PRISM", "1.0.0")]
public class PrismModule
{
    [FMainFunction("Validate SN against PRISM MES. PASS=valid, FAIL=invalid/not found. param1=SN, param2=workOrder", "PRISM")]
    public void prism_validate_sn(string sn = "", string workOrder = "")
    {
        var ctx = TestContext.Current.Value;
        var svc = PrismService.Instance;
        if (svc == null) { Fail(ctx, "PrismService not available"); return; }

        var (ok, msg) = svc.ValidateSN(sn, workOrder);
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = ok; }
    }

    [FMainFunction("Save PASS/FAIL result to PRISM MES. param1=SN, param2=PASS|FAIL, param3=test result summary", "PRISM")]
    public void prism_set_result(string sn = "", string passFail = "PASS", string testResult = "")
    {
        var ctx = TestContext.Current.Value;
        var svc = PrismService.Instance;
        if (svc == null) { Fail(ctx, "PrismService not available"); return; }

        var msg = svc.SaveTestResult(sn, passFail, testResult);
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = msg.StartsWith("SUCCESS"); }
    }

    [FMainFunction("Query work order info from PRISM MES; writes process qty to Measure column. param1=workOrder", "PRISM")]
    public void prism_get_work_order(string workOrder = "")
    {
        var ctx = TestContext.Current.Value;
        var svc = PrismService.Instance;
        if (svc == null) { Fail(ctx, "PrismService not available"); return; }

        var arr = svc.GetWorkOrderInfo(workOrder);
        if (ctx != null)
        {
            ctx.MeasureValue = (arr != null && arr.Length > 4) ? arr[4] : (svc.IsDebugMode ? "Debug" : "N/A");
            ctx.ForceResult  = arr != null || svc.IsDebugMode;
        }
    }

    [FMainFunction("Pre-process check: validate SN then set ForceResult false if PRISM rejects it. param1=SN, param2=workOrder", "PRISM")]
    public void prism_check_process(string sn = "", string workOrder = "")
    {
        var ctx = TestContext.Current.Value;
        var svc = PrismService.Instance;
        if (svc == null) { Fail(ctx, "PrismService not available"); return; }

        var (ok, msg) = svc.ValidateSN(sn, workOrder);
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = ok; }
    }

    private static void Fail(TestContext? ctx, string msg)
    {
        if (ctx != null) { ctx.MeasureValue = msg; ctx.ForceResult = false; }
    }
}
