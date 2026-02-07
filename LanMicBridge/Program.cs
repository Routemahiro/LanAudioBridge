using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanMicBridge;

static class Program
{
    // ── 同時起動防止 + トレイ復帰用 ──────────────────────────────
    private const string MutexName = "Global\\LanMicBridge_SingleInstance";
    private const string ShowEventName = "Global\\LanMicBridge_ShowWindow";

    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 既に起動中 → 既存インスタンスを復帰させて終了
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return;
        }

        try
        {
            // トレイ格納中でも2回目起動で復帰できるようイベントを作成
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1(showEvent));
        }
        finally
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch { }
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
    }

    /// <summary>
    /// Mutex を解放 → 新プロセスを起動 → 現在のプロセスを強制終了する。
    /// Form1.RestartApplication() から呼ばれる。
    /// </summary>
    internal static void Restart()
    {
        // Mutex を解放して新プロセスが起動できるようにする
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
        catch { }

        // 新プロセスを起動
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true
            });
        }
        catch { }

        // 現在のプロセスを強制終了
        Environment.Exit(0);
    }

    /// <summary>
    /// 既に起動中のインスタンスに「ウィンドウを表示して」と通知する。
    /// EventWaitHandle（トレイ格納中対応）→ フォールバックで SetForegroundWindow。
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ShowEventName);
            evt.Set();
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // イベントがまだ作成されていない場合はフォールバック
        }

        ActivateExistingInstance();
    }

    /// <summary>
    /// 既に起動中のインスタンスを検出し、ウィンドウを前面に復帰させる。
    /// 最小化中の場合は復元してから前面に出す。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();

        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            if (proc.Id == current.Id) continue;
            if (proc.MainWindowHandle == IntPtr.Zero) continue;

            if (IsIconic(proc.MainWindowHandle))
            {
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
            }

            SetForegroundWindow(proc.MainWindowHandle);
            break;
        }
    }
}
