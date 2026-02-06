using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanMicBridge;

static class Program
{
    // ── 同時起動防止用 ──────────────────────────────
    private const string MutexName = "Global\\LanMicBridge_SingleInstance";

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
        using var mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 既に起動中 → 既存ウィンドウを前面に出して終了
            ActivateExistingInstance();
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
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

            // 最小化されていたら復元
            if (IsIconic(proc.MainWindowHandle))
            {
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
            }

            SetForegroundWindow(proc.MainWindowHandle);
            break;
        }
    }
}
