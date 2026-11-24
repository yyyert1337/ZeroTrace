using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ApexUninstaller.Models;
using Microsoft.Win32;

namespace ApexUninstaller.Services
{
    public class LeftoverScanner
    {
        private static readonly HashSet<string> BlacklistedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "microsoft", "system", "system32", "syswow64", "common files",
            "program files", "program files (x86)", "temp", "tmp", "desktop", "appdata",
            "local", "roaming", "locallow", "user", "users", "default", "public",
            "google", "apple", "adobe", "intel", "amd", "nvidia", "steam", "epic games",
            "bin", "cache", "data", "logs", "config", "install", "installer", "setup",
            "helper", "package", "packages", "service", "services", "driver", "drivers",
            "programs", "crashdumps", "d3dscache", "comms", "connecteddevicesplatform",
            "softwaredistribution", "usoprivate", "usoshared", "peerdistrepub",
            "placeholdertilelogofolder", "npm-cache", "ssh", "system volume information",
            "$recycle.bin", "recovery"
        };

        private static readonly HashSet<string> IgnoredContainerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "programs", "microsoft", "microsoft_corporation", "windows", "temp", "squirreltemp",
            "packages", "package cache", "crashdumps", "d3dscache", "comms",
            "connecteddevicesplatform", "softwaredistribution", "usoprivate", "usoshared",
            "peerdistrepub", "placeholdertilelogofolder", "cache", "npm-cache", "ssh",
            "system volume information", "$recycle.bin", "recovery", "common files",
            "adobe", "nvidia", "nvidia corporation", "intel", "amd"
        };

        public async Task<List<LeftoverItem>> ScanLeftoversAsync(InstalledApp app)
        {
            return await Task.Run(() =>
            {
                var traces = new List<LeftoverItem>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (app == null) return traces;

                // 1. Generate search tokens
                var tokens = ExtractSearchTokens(app);

                // 2. Check InstallLocation
                if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                {
                    if (IsSafeFolderToDelete(app.InstallLocation))
                    {
                        long size = CalculateFolderSize(app.InstallLocation);
                        AddTrace(traces, seenPaths, new LeftoverItem
                        {
                            Type = LeftoverType.Directory,
                            Path = app.InstallLocation,
                            DescriptionRu = "Основная папка установки программы",
                            DescriptionEn = "Main program installation directory",
                            SizeBytes = size,
                            IsSelected = true
                        });
                    }
                }

                // 3. Scan Common System Data Folders
                var baseFolders = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };

                foreach (var baseFolder in baseFolders)
                {
                    if (string.IsNullOrWhiteSpace(baseFolder) || !Directory.Exists(baseFolder))
                        continue;

                    ScanFolderForTokens(baseFolder, tokens, app.Publisher, traces, seenPaths);
                }

                // 4. Scan Shortcuts (Desktop & Start Menu)
                var shortcutFolders = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
                };

                foreach (var scFolder in shortcutFolders)
                {
                    if (Directory.Exists(scFolder))
                    {
                        ScanShortcuts(scFolder, tokens, traces, seenPaths);
                    }
                }

                // 5. Scan Registry Keys
                ScanRegistryHives(app, tokens, traces, seenPaths);

                // 6. Scan Startup / Run Registry Entries
                ScanStartupRunEntries(app, tokens, traces, seenPaths);

                // 7. Scan Scheduled Tasks (updaters and background tasks)
                ScanScheduledTasks(app, tokens, traces, seenPaths);

                // 8. Scan Windows Services and Drivers
                ScanWindowsServices(app, tokens, traces, seenPaths);

                return traces;
            });
        }

        private List<string> ExtractSearchTokens(InstalledApp app)
        {
            var tokens = new List<string>();
            if (app == null) return tokens;

            string raw = app.DisplayName ?? "";
            // Remove parentheticals like (Cracked), (x64), (Portable), (user), etc.
            string clean = Regex.Replace(raw, @"\([^)]*\)", " ");
            // Remove version strings like 7.23, v1.0, 11.0.3-0, etc.
            clean = Regex.Replace(clean, @"\b(?:version|v)?\d+[\d\.\-+_]*\b", " ", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\s+", " ").Trim();

            if (!string.IsNullOrWhiteSpace(clean) && clean.Length >= 3 && !BlacklistedTokens.Contains(clean))
            {
                tokens.Add(clean);
            }

            // Compact version (e.g. "Zen Browser" -> "ZenBrowser", "Prism Launcher" -> "PrismLauncher")
            string compact = clean.Replace(" ", "");
            if (compact.Length >= 3 && !compact.Equals(clean, StringComparison.OrdinalIgnoreCase) && !BlacklistedTokens.Contains(compact))
            {
                if (!tokens.Contains(compact, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(compact);
                }
            }

            // Generic product suffix stripping (e.g. "Zen Browser" -> "Zen", "Prism Launcher" -> "Prism")
            string[] genericSuffixes = new string[] { " browser", " launcher", " client", " desktop", " application", " app", " software", " suite", " tool", " tools" };
            foreach (var suf in genericSuffixes)
            {
                if (clean.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = clean.Substring(0, clean.Length - suf.Length).Trim();
                    if (baseName.Length >= 3 && !BlacklistedTokens.Contains(baseName) && !tokens.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                    {
                        tokens.Add(baseName);
                        string compactBase = baseName.Replace(" ", "");
                        if (compactBase.Length >= 3 && !tokens.Contains(compactBase, StringComparer.OrdinalIgnoreCase) && !BlacklistedTokens.Contains(compactBase))
                        {
                            tokens.Add(compactBase);
                        }
                    }
                }
            }

            // Subkey ID if it's not a GUID
            if (!string.IsNullOrWhiteSpace(app.Id) && !app.Id.StartsWith("{") && app.Id.Length >= 3 && !BlacklistedTokens.Contains(app.Id))
            {
                if (!tokens.Contains(app.Id, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(app.Id);
                }
            }

            return tokens;
        }

        private void ScanFolderForTokens(string root, List<string> tokens, string publisher, List<LeftoverItem> traces, HashSet<string> seen)
        {
            try
            {
                var dirInfo = new DirectoryInfo(root);
                foreach (var sub in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    string subName = sub.Name;

                    bool isContainer = IgnoredContainerDirs.Contains(subName);

                    if (!isContainer)
                    {
                        // Direct match on App name token
                        foreach (var token in tokens)
                        {
                            if (subName.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                                (token.Length >= 4 && (subName.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
                                                      subName.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) ||
                                                      subName.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase))) ||
                                (token.Length >= 6 && subName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 && subName.Length <= token.Length + 4))
                            {
                                if (IsSafeFolderToDelete(sub.FullName))
                                {
                                    AddTrace(traces, seen, new LeftoverItem
                                    {
                                        Type = LeftoverType.Directory,
                                        Path = sub.FullName,
                                        DescriptionRu = "Папка с данными программы",
                                        DescriptionEn = "Application data directory",
                                        SizeBytes = CalculateFolderSize(sub.FullName),
                                        IsSelected = true
                                    });
                                }
                                break;
                            }
                        }
                    }

                    // Special case: AppData\Local\Programs can contain user-installed app directories
                    if (subName.Equals("Programs", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (var progSub in sub.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                            {
                                foreach (var token in tokens)
                                {
                                    if (progSub.Name.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                                        (token.Length >= 4 && (progSub.Name.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
                                                              progSub.Name.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) ||
                                                              progSub.Name.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase))))
                                    {
                                        if (IsSafeFolderToDelete(progSub.FullName))
                                        {
                                            AddTrace(traces, seen, new LeftoverItem
                                            {
                                                Type = LeftoverType.Directory,
                                                Path = progSub.FullName,
                                                DescriptionRu = "Папка установки программы",
                                                DescriptionEn = "Application program directory",
                                                SizeBytes = CalculateFolderSize(progSub.FullName),
                                                IsSelected = true
                                            });
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // If folder matches publisher, scan inside it for app token
                    if (!string.IsNullOrWhiteSpace(publisher) && publisher.Length >= 3 && !BlacklistedTokens.Contains(publisher))
                    {
                        if (subName.Equals(publisher, StringComparison.OrdinalIgnoreCase) ||
                            (subName.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0 && subName.Length <= publisher.Length + 10) ||
                            (publisher.IndexOf(subName, StringComparison.OrdinalIgnoreCase) >= 0 && publisher.Length <= subName.Length + 10))
                        {
                            try
                            {
                                foreach (var inner in sub.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                                {
                                    foreach (var token in tokens)
                                    {
                                        if (inner.Name.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                                            (token.Length >= 4 && (inner.Name.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
                                                                  inner.Name.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) ||
                                                                  inner.Name.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase))) ||
                                            (token.Length >= 6 && inner.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 && inner.Name.Length <= token.Length + 4))
                                        {
                                            if (IsSafeFolderToDelete(inner.FullName))
                                            {
                                                AddTrace(traces, seen, new LeftoverItem
                                                {
                                                    Type = LeftoverType.Directory,
                                                    Path = inner.FullName,
                                                    DescriptionRu = "Папка издателя (" + publisher + ")",
                                                    DescriptionEn = "Publisher folder (" + publisher + ")",
                                                    SizeBytes = CalculateFolderSize(inner.FullName),
                                                    IsSelected = true
                                                });
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void ScanShortcuts(string folder, List<string> tokens, List<LeftoverItem> traces, HashSet<string> seen)
        {
            try
            {
                foreach (var file in Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    string fn = Path.GetFileNameWithoutExtension(file);
                    foreach (var token in tokens)
                    {
                        if (fn.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            AddTrace(traces, seen, new LeftoverItem
                            {
                                Type = LeftoverType.Shortcut,
                                Path = file,
                                DescriptionRu = "Ярлык на рабочем столе или в меню Пуск",
                                DescriptionEn = "Desktop or Start Menu shortcut",
                                SizeBytes = new FileInfo(file).Length,
                                IsSelected = true
                            });
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanRegistryHives(InstalledApp app, List<string> tokens, List<LeftoverItem> traces, HashSet<string> seen)
        {
            // 1. Uninstall registration key itself
            if (!string.IsNullOrWhiteSpace(app.RegistryKeyPath))
            {
                var hive = app.RegistryHiveName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                string fullPath = app.RegistryHiveName + "\\" + app.RegistryKeyPath;
                if (CheckRegistryKeyExists(hive, app.RegistryKeyPath))
                {
                    AddTrace(traces, seen, new LeftoverItem
                    {
                        Type = LeftoverType.RegistryKey,
                        Path = fullPath,
                        RegistryHive = app.RegistryHiveName,
                        RegistrySubKey = app.RegistryKeyPath,
                        DescriptionRu = "Запись программы в списке установленных программ Windows",
                        DescriptionEn = "Application entry in Windows installed software list",
                        IsSelected = true
                    });
                }
            }

            // 2. HKCU & HKLM Software keys
            var hives = new[]
            {
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Root = @"Software" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Root = @"SOFTWARE" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Root = @"SOFTWARE" }
            };

            foreach (var h in hives)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(h.Hive, h.View))
                    using (var rootKey = baseKey.OpenSubKey(h.Root))
                    {
                        if (rootKey == null) continue;

                        foreach (var subName in rootKey.GetSubKeyNames())
                        {
                            // Match top-level software subkey
                            foreach (var token in tokens)
                            {
                                if (subName.Equals(token, StringComparison.OrdinalIgnoreCase))
                                {
                                    string fullReg = h.Name + "\\" + h.Root + "\\" + subName;
                                    AddTrace(traces, seen, new LeftoverItem
                                    {
                                        Type = LeftoverType.RegistryKey,
                                        Path = fullReg,
                                        RegistryHive = h.Name,
                                        RegistrySubKey = h.Root + "\\" + subName,
                                        DescriptionRu = "Раздел настроек программы в реестре",
                                        DescriptionEn = "Program settings key in Windows Registry",
                                        IsSelected = true
                                    });
                                }
                            }

                            // Match Publisher\AppName
                            if (!string.IsNullOrWhiteSpace(app.Publisher) && app.Publisher.Length >= 4 && !BlacklistedTokens.Contains(app.Publisher))
                            {
                                if (subName.IndexOf(app.Publisher, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try
                                    {
                                        using (var pubKey = rootKey.OpenSubKey(subName))
                                        {
                                            if (pubKey != null)
                                            {
                                                foreach (var innerName in pubKey.GetSubKeyNames())
                                                {
                                                    foreach (var token in tokens)
                                                    {
                                                        if (innerName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                                        {
                                                            string fullReg = h.Name + "\\" + h.Root + "\\" + subName + "\\" + innerName;
                                                            AddTrace(traces, seen, new LeftoverItem
                                                            {
                                                                Type = LeftoverType.RegistryKey,
                                                                Path = fullReg,
                                                                RegistryHive = h.Name,
                                                                RegistrySubKey = h.Root + "\\" + subName + "\\" + innerName,
                                                                DescriptionRu = "Раздел реестра издателя (" + app.Publisher + ")",
                                                                DescriptionEn = "Publisher registry key (" + app.Publisher + ")",
                                                                IsSelected = true
                                                            });
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void ScanStartupRunEntries(InstalledApp app, List<string> tokens, List<LeftoverItem> traces, HashSet<string> seen)
        {
            var runKeys = new[]
            {
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, SubKey = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" }
            };

            foreach (var rk in runKeys)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(rk.Hive, RegistryView.Default))
                    using (var key = baseKey.OpenSubKey(rk.SubKey))
                    {
                        if (key == null) continue;

                        foreach (var valName in key.GetValueNames())
                        {
                            string valData = key.GetValue(valName) as string ?? "";
                            bool matches = false;

                            foreach (var token in tokens)
                            {
                                if (valName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    valData.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matches = true;
                                    break;
                                }
                            }

                            if (!matches && !string.IsNullOrWhiteSpace(app.InstallLocation))
                            {
                                if (valData.IndexOf(app.InstallLocation, StringComparison.OrdinalIgnoreCase) >= 0)
                                    matches = true;
                            }

                            if (matches)
                            {
                                string fullValPath = rk.Name + "\\" + rk.SubKey + " -> " + valName;
                                AddTrace(traces, seen, new LeftoverItem
                                {
                                    Type = LeftoverType.RegistryValue,
                                    Path = fullValPath,
                                    RegistryHive = rk.Name,
                                    RegistrySubKey = rk.SubKey,
                                    RegistryValueName = valName,
                                    DescriptionRu = "Параметр автозагрузки в реестре",
                                    DescriptionEn = "Windows startup entry in registry",
                                    IsSelected = true
                                });
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void ScanScheduledTasks(InstalledApp app, List<string> tokens, List<LeftoverItem> traces, HashSet<string> seenPaths)
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string tasksDir = Path.Combine(sysDir, "Tasks");
                if (!Directory.Exists(tasksDir)) return;

                string appLoc = !string.IsNullOrWhiteSpace(app.InstallLocation) ? app.InstallLocation.TrimEnd('\\') : null;
                bool hasLoc = !string.IsNullOrEmpty(appLoc) && appLoc.Length > 4;

                var taskFiles = Directory.GetFiles(tasksDir, "*", SearchOption.AllDirectories);
                foreach (var file in taskFiles)
                {
                    try
                    {
                        string relPath = file.Substring(tasksDir.Length);
                        string taskPath = "\\" + relPath.TrimStart('\\');
                        string taskName = Path.GetFileName(file);

                        // Skip core Windows tasks unless task points to app directory
                        bool isCoreMsTask = taskPath.IndexOf(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool matchesApp = false;
                        string matchedReason = "";

                        string fileContent = null;
                        try
                        {
                            fileContent = File.ReadAllText(file);
                        }
                        catch { }

                        // 1. If task references app.InstallLocation -> definite match
                        if (hasLoc && fileContent != null && fileContent.IndexOf(appLoc, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matchesApp = true;
                            matchedReason = "Задание планировщика запускает компоненты программы";
                        }

                        // 2. If taskName or taskPath matches app tokens (and not core MS task)
                        if (!matchesApp && !isCoreMsTask)
                        {
                            foreach (var token in tokens)
                            {
                                if (taskName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    taskPath.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matchesApp = true;
                                    matchedReason = "Задание планировщика: " + taskName;
                                    break;
                                }
                            }
                        }

                        if (matchesApp)
                        {
                            AddTrace(traces, seenPaths, new LeftoverItem
                            {
                                Type = LeftoverType.Task,
                                Path = taskPath,
                                DescriptionRu = string.IsNullOrEmpty(matchedReason) ? "Задание автообновления / запуска: " + taskName : matchedReason,
                                DescriptionEn = "Scheduled task: " + taskName,
                                RegistrySubKey = taskPath,
                                SizeBytes = 0,
                                IsSelected = true
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ScanWindowsServices(InstalledApp app, List<string> tokens, List<LeftoverItem> traces, HashSet<string> seenPaths)
        {
            try
            {
                string appLoc = !string.IsNullOrWhiteSpace(app.InstallLocation) ? app.InstallLocation.TrimEnd('\\') : null;
                bool hasLoc = !string.IsNullOrEmpty(appLoc) && appLoc.Length > 4;

                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (servicesKey == null) return;

                    foreach (var svcName in servicesKey.GetSubKeyNames())
                    {
                        try
                        {
                            if (BlacklistedTokens.Contains(svcName)) continue;

                            using (var svcKey = servicesKey.OpenSubKey(svcName))
                            {
                                if (svcKey == null) continue;

                                string imagePath = svcKey.GetValue("ImagePath") as string;
                                string dispName = svcKey.GetValue("DisplayName") as string;
                                string cleanImage = !string.IsNullOrEmpty(imagePath)
                                    ? imagePath.Trim('\"', ' ').Replace(@"\??\", "")
                                    : "";

                                bool matchesApp = false;
                                string matchedReason = "";

                                // 1. Direct path check against app.InstallLocation
                                if (hasLoc && !string.IsNullOrEmpty(cleanImage) && cleanImage.StartsWith(appLoc, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchesApp = true;
                                    matchedReason = string.Format("Служба программы ({0})", string.IsNullOrEmpty(dispName) ? svcName : dispName);
                                }

                                // 2. Match service name or display name against app tokens (excluding Windows system files)
                                if (!matchesApp && !string.IsNullOrEmpty(cleanImage) && cleanImage.IndexOf(@"\Windows\", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    foreach (var token in tokens)
                                    {
                                        if (svcName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            (!string.IsNullOrEmpty(dispName) && dispName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                                        {
                                            matchesApp = true;
                                            matchedReason = string.Format("Оставшаяся служба ({0})", string.IsNullOrEmpty(dispName) ? svcName : dispName);
                                            break;
                                        }
                                    }
                                }

                                if (matchesApp)
                                {
                                    AddTrace(traces, seenPaths, new LeftoverItem
                                    {
                                        Type = LeftoverType.Service,
                                        Path = svcName,
                                        DescriptionRu = matchedReason,
                                        DescriptionEn = "Windows service: " + (string.IsNullOrEmpty(dispName) ? svcName : dispName),
                                        RegistryHive = "HKLM",
                                        RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\" + svcName,
                                        SizeBytes = 0,
                                        IsSelected = true
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        #region Win32 Native APIs for Force Deletion & Process Unlocking

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
        private const int RmForceShutdown = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
        private static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[] rgsFilenames,
            uint nApplications,
            IntPtr rgApplications,
            uint nServices,
            string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmShutdown(uint pSessionHandle, int lActionFlags, IntPtr fnStatus);

        #endregion

        public bool DeleteLeftoverItem(LeftoverItem item, out string error)
        {
            error = null;
            if (item == null) return true;

            try
            {
                switch (item.Type)
                {
                    case LeftoverType.Directory:
                        return ForceDeleteDirectory(item.Path, out error);

                    case LeftoverType.File:
                    case LeftoverType.Shortcut:
                        return ForceDeleteFile(item.Path, out error);

                    case LeftoverType.Task:
                        return DeleteScheduledTask(item.Path, out error);

                    case LeftoverType.Service:
                        return DeleteWindowsService(item.Path, out error);

                    case LeftoverType.RegistryKey:
                        return DeleteRegistrySubKeyTree(item.RegistryHive, item.RegistrySubKey, out error);

                    case LeftoverType.RegistryValue:
                        return DeleteRegistryValue(item.RegistryHive, item.RegistrySubKey, item.RegistryValueName, out error);

                    default:
                        return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string GetProcessMainModulePath(Process proc)
        {
            try
            {
                var mm = proc.MainModule;
                if (mm != null && !string.IsNullOrEmpty(mm.FileName))
                {
                    return mm.FileName;
                }
            }
            catch { }

            try
            {
                IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, proc.Id);
                if (hProc != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                        {
                            return sb.ToString();
                        }
                    }
                    finally
                    {
                        CloseHandle(hProc);
                    }
                }
            }
            catch { }

            return null;
        }

        public static void StopServicesAndDriversInPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return;
            try
            {
                string fullPath = Path.GetFullPath(targetPath).TrimEnd('\\');
                string fullDir = fullPath + "\\";

                using (var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (servicesKey == null) return;
                    foreach (var serviceName in servicesKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (var subKey = servicesKey.OpenSubKey(serviceName))
                            {
                                if (subKey == null) continue;
                                string imagePath = subKey.GetValue("ImagePath") as string;
                                if (string.IsNullOrEmpty(imagePath)) continue;

                                imagePath = Environment.ExpandEnvironmentVariables(imagePath).Trim('"', ' ');
                                if (imagePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
                                {
                                    imagePath = imagePath.Substring(4);
                                }

                                if (imagePath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(imagePath, fullPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Stop service / driver
                                    try
                                    {
                                        var psi = new ProcessStartInfo("sc.exe", "stop \"" + serviceName + "\"")
                                        {
                                            CreateNoWindow = true,
                                            UseShellExecute = false,
                                            WindowStyle = ProcessWindowStyle.Hidden
                                        };
                                        using (var p = Process.Start(psi))
                                        {
                                            p.WaitForExit(3000);
                                        }
                                    }
                                    catch { }

                                    // Delete service entry
                                    try
                                    {
                                        var psi = new ProcessStartInfo("sc.exe", "delete \"" + serviceName + "\"")
                                        {
                                            CreateNoWindow = true,
                                            UseShellExecute = false,
                                            WindowStyle = ProcessWindowStyle.Hidden
                                        };
                                        using (var p = Process.Start(psi))
                                        {
                                            p.WaitForExit(2000);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void ForceKillProcessesInAndAroundPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return;
            try
            {
                string fullPath = Path.GetFullPath(targetPath).TrimEnd('\\');
                string fullPathWithSlash = fullPath + "\\";

                var prefixes = new List<string> { fullPathWithSlash };

                // If targetPath is inside an application folder, also check the parent app folder
                try
                {
                    var parent = Directory.GetParent(fullPath);
                    if (parent != null && IsSafeFolderToDelete(parent.FullName))
                    {
                        prefixes.Add(parent.FullName.TrimEnd('\\') + "\\");
                    }
                }
                catch { }

                int myPid = Process.GetCurrentProcess().Id;

                foreach (var proc in Process.GetProcesses())
                {
                    if (proc.Id == myPid || proc.Id <= 4) continue;
                    try
                    {
                        string procPath = GetProcessMainModulePath(proc);
                        if (string.IsNullOrEmpty(procPath)) continue;

                        bool match = false;
                        if (string.Equals(procPath, fullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                        }
                        else
                        {
                            for (int i = 0; i < prefixes.Count; i++)
                            {
                                if (procPath.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                                {
                                    match = true;
                                    break;
                                }
                            }
                        }

                        if (match)
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(1000);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void UnlockResourcesUsingRestartManager(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                uint session;
                string key = Guid.NewGuid().ToString();
                int res = RmStartSession(out session, 0, key);
                if (res == 0)
                {
                    try
                    {
                        string[] resources = new string[] { path };
                        res = RmRegisterResources(session, (uint)resources.Length, resources, 0, IntPtr.Zero, 0, null);
                        if (res == 0)
                        {
                            RmShutdown(session, RmForceShutdown, IntPtr.Zero);
                        }
                    }
                    finally
                    {
                        RmEndSession(session);
                    }
                }
            }
            catch { }
        }

        private static void MakeDirectoryDeletable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return;
                var di = new DirectoryInfo(dirPath);
                if ((di.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    di.Attributes = FileAttributes.Normal;
                }

                foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            file.Attributes = FileAttributes.Normal;
                        }
                    }
                    catch { }
                }

                foreach (var subDir in di.GetDirectories("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if ((subDir.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            subDir.Attributes = FileAttributes.Normal;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void DeleteDirectoryContentsBottomUp(string dirPath)
        {
            var di = new DirectoryInfo(dirPath);
            if (!di.Exists) return;

            // Delete all files first
            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                }
                catch
                {
                    try
                    {
                        UnlockResourcesUsingRestartManager(file.FullName);
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                    catch
                    {
                        MoveFileEx(file.FullName, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                }
            }

            // Delete subdirectories from deepest to shallowest
            var subDirs = di.GetDirectories("*", SearchOption.AllDirectories)
                            .OrderByDescending(d => d.FullName.Length)
                            .ToList();

            foreach (var sub in subDirs)
            {
                try
                {
                    if (sub.Exists)
                    {
                        sub.Attributes = FileAttributes.Normal;
                        sub.Delete(true);
                    }
                }
                catch
                {
                    try
                    {
                        UnlockResourcesUsingRestartManager(sub.FullName);
                        sub.Attributes = FileAttributes.Normal;
                        sub.Delete(true);
                    }
                    catch
                    {
                        MoveFileEx(sub.FullName, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                }
            }
        }

        public static bool ForceDeleteDirectory(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (!Directory.Exists(path)) return true;

            if (!IsSafeFolderToDelete(path))
            {
                error = "Папка защищена от удаления в целях безопасности";
                return false;
            }

            // 0. Stop and delete any background Windows services or kernel drivers running from this path
            StopServicesAndDriversInPath(path);

            // 1. Force kill all processes in this directory (and parent app folder if applicable)
            ForceKillProcessesInAndAroundPath(path);

            // 2. Unlock handles with Restart Manager
            UnlockResourcesUsingRestartManager(path);

            // 3. Clear file and directory attributes (strip ReadOnly, Hidden, System)
            MakeDirectoryDeletable(path);

            // 4. Retry loop with bottom-up deletion
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    DeleteDirectoryContentsBottomUp(path);

                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }

                    if (!Directory.Exists(path))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    ForceKillProcessesInAndAroundPath(path);
                    UnlockResourcesUsingRestartManager(path);
                    Thread.Sleep(100 * (attempt + 1));
                }
            }

            if (!Directory.Exists(path)) return true;

            // Schedule for reboot deletion if still exists
            try
            {
                MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                return true;
            }
            catch { }

            return false;
        }

        public static bool ForceDeleteFile(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (!File.Exists(path)) return true;

            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch
            {
                StopServicesAndDriversInPath(path);
                ForceKillProcessesInAndAroundPath(path);
                UnlockResourcesUsingRestartManager(path);

                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try
                    {
                        MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                        return true;
                    }
                    catch { }
                    return false;
                }
            }
        }

        public static bool DeleteScheduledTask(string taskPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(taskPath)) return true;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = string.Format("/Delete /TN \"{0}\" /F", taskPath),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(4000);
                        if (proc.ExitCode == 0) return true;
                        error = proc.StandardError.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // Fallback: direct task file deletion in C:\Windows\System32\Tasks
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string taskFile = Path.Combine(sysDir, "Tasks" + (taskPath.StartsWith("\\") ? taskPath : "\\" + taskPath));
                if (File.Exists(taskFile))
                {
                    string fErr;
                    if (ForceDeleteFile(taskFile, out fErr))
                        return true;
                }
            }
            catch { }

            return false;
        }

        public static bool DeleteWindowsService(string svcName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(svcName)) return true;

            try
            {
                // 1. Force stop service / driver if active
                try
                {
                    var stopPsi = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = string.Format("stop \"{0}\"", svcName),
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using (var sp = Process.Start(stopPsi))
                    {
                        if (sp != null) sp.WaitForExit(3000);
                    }
                }
                catch { }

                // 2. Delete service registration
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = string.Format("delete \"{0}\"", svcName),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(4000);
                        if (proc.ExitCode == 0)
                        {
                            string dummyErr;
                            RegeditHelper.ForceDeleteRegistryTree("HKLM", @"SYSTEM\CurrentControlSet\Services\" + svcName, out dummyErr);
                            return true;
                        }
                        error = proc.StandardError.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // 3. Fallback: force wipe from registry directly
            string regErr;
            if (RegeditHelper.ForceDeleteRegistryTree("HKLM", @"SYSTEM\CurrentControlSet\Services\" + svcName, out regErr))
            {
                return true;
            }

            return false;
        }

        private bool DeleteRegistrySubKeyTree(string hiveName, string subKeyPath, out string error)
        {
            return RegeditHelper.ForceDeleteRegistryTree(hiveName, subKeyPath, out error);
        }

        private bool DeleteRegistryValue(string hiveName, string subKeyPath, string valueName, out string error)
        {
            error = null;
            try
            {
                var hive = hiveName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(subKeyPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool CheckRegistryKeyExists(RegistryHive hive, string subKeyPath)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var k = baseKey.OpenSubKey(subKeyPath))
                {
                    if (k != null) return true;
                }
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32))
                using (var k = baseKey.OpenSubKey(subKeyPath))
                {
                    if (k != null) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsSafeFolderToDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                path = Path.GetFullPath(path).TrimEnd('\\');
            }
            catch { return false; }

            if (path.Length <= 4) return false;

            string root = Path.GetPathRoot(path).TrimEnd('\\');
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return false;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string usersDir = null;
            try
            {
                var parent = Directory.GetParent(userProfile);
                if (parent != null) usersDir = parent.FullName;
            }
            catch { }

            var forbidden = new List<string>
            {
                winDir,
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Path.Combine(winDir, "SysWOW64"),
                Path.Combine(winDir, "WinSxS"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                userProfile,
                usersDir,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(root, "Recovery"),
                Path.Combine(root, "$Recycle.Bin"),
                Path.Combine(root, "System Volume Information")
            };

            foreach (var f in forbidden)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (string.Equals(path, f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static long CalculateFolderSize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
            long totalSize = 0;

            try
            {
                var stack = new Stack<Tuple<string, int>>();
                stack.Push(Tuple.Create(path, 0));

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    string curDir = item.Item1;
                    int depth = item.Item2;
                    if (depth > 30) continue;

                    try
                    {
                        var dirInfo = new DirectoryInfo(curDir);
                        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        foreach (var file in dirInfo.EnumerateFiles())
                        {
                            totalSize += file.Length;
                        }

                        foreach (var sub in dirInfo.EnumerateDirectories())
                        {
                            if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                stack.Push(Tuple.Create(sub.FullName, depth + 1));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return totalSize;
        }

        private static void AddTrace(List<LeftoverItem> list, HashSet<string> seen, LeftoverItem item)
        {
            if (!seen.Contains(item.Path))
            {
                seen.Add(item.Path);
                list.Add(item);
            }
        }
    }
}
