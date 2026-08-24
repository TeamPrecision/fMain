using System.Runtime.InteropServices;
using System.Text;

namespace fMain.Services;

/// <summary>
/// Captures USB HID barcode scanner input (keyboard-emulation mode) via a
/// Windows low-level keyboard hook.  Module code calls WaitForScanAsync.
/// </summary>
public sealed class BarcodeService : IDisposable
{
    // Static accessor so Roslyn-compiled modules can reach the singleton.
    public static BarcodeService? Instance { get; private set; }

    // ── Win32 ─────────────────────────────────────────────────────────────────

    private const int  WH_KEYBOARD_LL = 13;
    private const int  WM_KEYDOWN     = 0x0100;
    private const uint WM_QUIT        = 0x0012;
    private const uint VK_RETURN      = 0x0D;
    private const uint VK_BACK        = 0x08;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, ref KBDLLHOOKSTRUCT lp);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, ref KBDLLHOOKSTRUCT lp);
    [DllImport("user32.dll")] static extern int    GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool   TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool   PostThreadMessage(uint tid, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int    ToUnicode(uint vk, uint scan, byte[] state, StringBuilder buf, int size, uint flags);
    [DllImport("user32.dll")] static extern bool   GetKeyboardState(byte[] state);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // ── State ────────────────────────────────────────────────────────────────

    private IntPtr                     _hook = IntPtr.Zero;
    private LowLevelKeyboardProc?      _proc;          // keep reference → prevent GC
    private Thread?                    _thread;
    private uint                       _threadId;
    private readonly object            _lk  = new();
    private readonly StringBuilder     _buf = new(512);
    private TaskCompletionSource<string>? _tcs;
    private bool                       _disposed;

    // ── Startup ───────────────────────────────────────────────────────────────

    public BarcodeService()
    {
        Instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "BarcodeHookThread" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnKey;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    // ── Hook callback (called on _thread inside GetMessage) ───────────────────

    private IntPtr OnKey(int nCode, IntPtr wParam, ref KBDLLHOOKSTRUCT lp)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            lock (_lk)
            {
                if (_tcs != null && !_tcs.Task.IsCompleted)
                {
                    if (lp.vkCode == VK_RETURN)
                    {
                        _tcs.TrySetResult(_buf.ToString());
                        _tcs = null;
                        _buf.Clear();
                    }
                    else if (lp.vkCode == VK_BACK)
                    {
                        if (_buf.Length > 0) _buf.Length--;
                    }
                    else
                    {
                        var ks = new byte[256];
                        GetKeyboardState(ks);
                        var sb = new StringBuilder(4);
                        if (ToUnicode(lp.vkCode, lp.scanCode, ks, sb, 4, 0) > 0 && sb[0] >= 0x20)
                            _buf.Append(sb[0]);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, ref lp);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until the scanner sends a line (terminated with Enter).
    /// Cancellation causes OperationCanceledException.
    /// </summary>
    public async Task<string> WaitForScanAsync(CancellationToken ct = default)
    {
        TaskCompletionSource<string> tcs;
        lock (_lk)
        {
            _buf.Clear();
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = tcs;
        }

        await using (ct.Register(() =>
        {
            lock (_lk)
            {
                if (_tcs == tcs) { _tcs = null; _buf.Clear(); }
                tcs.TrySetCanceled();
            }
        }))
        {
            return await tcs.Task;
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Instance == this) Instance = null;
    }
}
