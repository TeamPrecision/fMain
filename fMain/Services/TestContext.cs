namespace fMain.Services;

/// <summary>
/// Per-step async-local context. Module functions read/write this to communicate
/// measured values and forced pass/fail results back to the runner.
/// </summary>
public class TestContext
{
    public static readonly AsyncLocal<TestContext?> Current = new();

    /// <summary>Value written to the Measure column. Set via test_write(value) or directly by a measurement function.</summary>
    public string MeasureValue { get; set; } = string.Empty;

    /// <summary>Null = auto (compare MeasureValue to Min/Max). True = force PASS. False = force FAIL.</summary>
    public bool? ForceResult { get; set; }

    public CancellationToken CancellationToken { get; set; }
}
