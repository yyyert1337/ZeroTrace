using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ApexUninstaller.Models;
using Microsoft.Win32;

namespace ApexUninstaller.Services
{
    public class ForceRemovalService
    {
        private static readonly HashSet<string> ProtectedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "services", "svchost", "taskhostw", "taskhost", "csrss", "lsass", "smss", "wininit",
            "winlogon", "dwm", "spoolsv", "runtimebroker", "sihost", "ctfmon", "fontdrvhost",
            "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
            "system", "idle", "registry", "zerotrace"
        };

        public async Task<int> TerminateAppProcessesAsync(InstalledApp app)
        {
            return await Task.Run(() =>
            {
                int killed = 0;
                if (app == null) return killed;

                int myPid = Process.GetCurrentProcess().Id;
                string loc = app.InstallLocation;
                bool hasLoc = !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc);

                // Collect known candidate process names from uninstaller / icon / app display name
                var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(app.DisplayName))
                {
                    string clean = Regex.Replace(app.DisplayName, @"\s+|\(.*?\)|\[.*?\]", "");
                    if (clean.Length >= 3 && !ProtectedProcessNames.Contains(clean))
                    {
                        candidateNames.Add(clean);
                    }
                }

                if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
                {
                    try
                    {
                        string iconExe = Path.GetFileNameWithoutExtension(app.DisplayIcon.Trim('\"', ' '));
                        if (!string.IsNullOrEmpty(iconExe) && iconExe.Length >= 3 && !ProtectedProcessNames.Contains(iconExe))
                        {
                            candidateNames.Add(iconExe);
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(app.UninstallString))
                {
                    try
                    {
                        string uninstExe, uninstArgs;
                        UninstallerService.ParseCommand(app.UninstallString, out uninstExe, out uninstArgs);
                        if (!string.IsNullOrEmpty(uninstExe))
                        {
                            string exeName = Path.GetFileNameWithoutExtension(uninstExe);
                            if (!string.IsNullOrEmpty(exeName) && exeName.Length >= 3 && !ProtectedProcessNames.Contains(exeName))
                            {
                                candidateNames.Add(exeName);
                            }
                        }
                    }
                    catch { }
                }

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.Id == myPid || proc.Id <= 4) continue;

                        string procName = proc.ProcessName;
                        if (ProtectedProcessNames.Contains(procName)) continue;

                        bool shouldKill = false;

                        // Check process main module path if accessible (safest and most accurate)
                        if (hasLoc)
                        {
                            try
                            {
                                if (proc.MainModule != null)
                                {
                                    string path = proc.MainModule.FileName;
                                    if (!string.IsNullOrEmpty(path) && path.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                                    {
                                        shouldKill = true;
                                    }
                                }
                            }
                            catch { }
                        }

                        // Check exact process name match against candidates (never substring matching)
                        if (!shouldKill && candidateNames.Contains(procName))
                        {
                            shouldKill = true;
                        }

                        if (shouldKill)
                        {
                            proc.Kill();
                            proc.WaitForExit(3000);
                            killed++;
                        }
                    }
                    catch
                    {
                    }
                }
                return killed;
            });
        }

        public bool RemoveEntryFromRegistry(InstalledApp app, out string error)
        {
            error = null;
            if (app.IsUwp)
            {
                error = AppLang.Instance.IsRussian
                    ? "UWP-приложения управляются через Windows AppX и не имеют прямой записи в реестре удаления."
                    : "UWP apps are managed via Windows AppX and do not have a direct uninstall registry key.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(app.RegistryKeyPath))
            {
                error = AppLang.Instance.IsRussian
                    ? "Путь к разделу реестра не найден."
                    : "Registry key path not found.";
                return false;
            }

            return RegeditHelper.ForceDeleteRegistryTree(app.RegistryHiveName, app.RegistryKeyPath, out error);
        }
    }
}
