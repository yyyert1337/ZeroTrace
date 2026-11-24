using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;

namespace ApexUninstaller
{
    public partial class App : Application
    {
        private const string MutexName = "ZeroTrace_SingleInstance_Mutex_B6A9F7E1";
        private static Mutex _singleInstanceMutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const int SW_SHOWNORMAL = 1;
        private const int SW_SHOW = 5;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                var ex = args.Exception;
                var sb = new StringBuilder();
                var cur = ex;
                while (cur != null)
                {
                    sb.AppendLine(cur.Message);
                    cur = cur.InnerException;
                }

                MessageBox.Show(
                    "Произошла непредвиденная ошибка:\n" + sb.ToString(),
                    "ZeroTrace",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                var sb = new StringBuilder();
                var cur = ex;
                while (cur != null)
                {
                    sb.AppendLine(cur.Message);
                    cur = cur.InnerException;
                }

                MessageBox.Show(
                    "Произошла ошибка:\n" + sb.ToString(),
                    "Ошибка ZeroTrace",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };

            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
            {
                BringExistingInstanceToFront();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private static void BringExistingInstanceToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var otherProcesses = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id)
                    .ToList();

                IntPtr targetHwnd = IntPtr.Zero;

                foreach (var proc in otherProcesses)
                {
                    uint targetPid = (uint)proc.Id;
                    EnumWindows((hWnd, lParam) =>
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == targetPid)
                        {
                            var sb = new StringBuilder(256);
                            GetWindowText(hWnd, sb, 256);
                            if (sb.ToString().IndexOf("ZeroTrace", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                targetHwnd = hWnd;
                                return false;
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (targetHwnd != IntPtr.Zero) break;

                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        targetHwnd = proc.MainWindowHandle;
                        break;
                    }
                }

                if (targetHwnd == IntPtr.Zero)
                {
                    EnumWindows((hWnd, lParam) =>
                    {
                        var sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        if (sb.ToString().IndexOf("ZeroTrace", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            targetHwnd = hWnd;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }

                if (targetHwnd != IntPtr.Zero)
                {
                    if (IsIconic(targetHwnd))
                    {
                        ShowWindowAsync(targetHwnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindowAsync(targetHwnd, SW_SHOWNORMAL);
                    }
                    SetForegroundWindow(targetHwnd);
                }
            }
            catch
            {
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
            base.OnExit(e);
        }

        public static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
