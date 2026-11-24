using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using ApexUninstaller.Models;
using Microsoft.Win32;

namespace ApexUninstaller.Services
{
    public class AutorunsScanner
    {
        public async Task<List<AutorunItem>> ScanAllAutorunsAsync(IProgress<int> progress)
        {
            return await Task.Run(() =>
            {
                var list = new List<AutorunItem>();

                // 1. Scan Registry Run & RunOnce (HKCU & HKLM)
                ScanRegistryRunKeys(list);
                ScanSafeBoot(list);
                ScanActiveSetup(list);
                ScanWinlogon(list);
                if (progress != null) progress.Report(25);

                // 2. Scan Startup Folders
                ScanStartupFolders(list);
                if (progress != null) progress.Report(40);

                // 3. Scan Scheduled Tasks
                ScanScheduledTasks(list);
                if (progress != null) progress.Report(65);

                // 4. Scan Services & Drivers
                ScanServicesAndDrivers(list);
                if (progress != null) progress.Report(80);

                // 5. Scan Explorer Context Menu Handlers
                ScanExplorerExtensions(list);
                if (progress != null) progress.Report(90);

                // 6. Scan Codecs, Drivers32, KnownDLLs
                ScanCodecsAndDrivers32(list);
                ScanKnownDLLs(list);
                if (progress != null) progress.Report(95);

                // 7. Enrich with FileVersionInfo, Icons, and File Existence Check
                EnrichItems(list);
                if (progress != null) progress.Report(100);

                return list.OrderBy(x => x.Category).ThenBy(x => x.EntryName).ToList();
            });
        }

        #region Scan Methods

        private void ScanRegistryRunKeys(List<AutorunItem> list)
        {
            var hives = new[]
            {
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Path = @"Software\Microsoft\Windows\CurrentVersion\Run" },
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Path = @"Software\Microsoft\Windows\CurrentVersion\RunOnce" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = @"Software\Microsoft\Windows\CurrentVersion\Run" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = @"Software\Microsoft\Windows\CurrentVersion\RunOnce" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce" },

                // Disabled counterparts (Sysinternals Autoruns format)
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Path = @"Software\Microsoft\Windows\CurrentVersion\Run\AutorunsDisabled" },
                new { Name = "HKCU", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Path = @"Software\Microsoft\Windows\CurrentVersion\AutorunsDisabled" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = @"Software\Microsoft\Windows\CurrentVersion\Run\AutorunsDisabled" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = @"Software\Microsoft\Windows\CurrentVersion\AutorunsDisabled" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run\AutorunsDisabled" },
                new { Name = "HKLM", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\AutorunsDisabled" }
            };

            foreach (var h in hives)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(h.Hive, h.View))
                    using (var key = baseKey.OpenSubKey(h.Path))
                    {
                        if (key == null) continue;

                        bool isDisabled = h.Path.IndexOf("AutorunsDisabled", StringComparison.OrdinalIgnoreCase) >= 0;

                        foreach (var valName in key.GetValueNames())
                        {
                            object val = key.GetValue(valName);
                            if (val == null) continue;

                            string rawVal = val.ToString();
                            string clean = CleanImagePath(rawVal);

                            bool isRunKey = h.Path.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!isDisabled && isRunKey)
                            {
                                try
                                {
                                    string appSub = h.Path.IndexOf("WOW6432Node", StringComparison.OrdinalIgnoreCase) >= 0
                                        ? @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
                                        : @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                                    using (var appKey = baseKey.OpenSubKey(appSub))
                                    {
                                        if (appKey != null)
                                        {
                                            byte[] b = appKey.GetValue(valName) as byte[];
                                            if (b != null && b.Length > 0 && b[0] != 0x02)
                                            {
                                                isDisabled = true;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            var item = new AutorunItem
                            {
                                Id = h.Name + "\\" + h.Path + "\\" + valName,
                                EntryName = string.IsNullOrEmpty(valName) ? "(Default)" : valName,
                                ImagePath = clean,
                                Arguments = ExtractArguments(rawVal, clean),
                                RegistryLocation = h.Name + "\\" + h.Path,
                                RegistryHive = h.Name,
                                RegistrySubKey = h.Path,
                                ValueName = valName,
                                Category = AutorunCategory.Logon,
                                IsEnabled = !isDisabled
                            };

                            lock (list)
                            {
                                list.Add(item);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void ScanSafeBoot(List<AutorunItem> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                {
                    // Active AlternateShell
                    using (var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot"))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue("AlternateShell");
                            if (val != null)
                            {
                                string rawVal = val.ToString();
                                string clean = CleanImagePath(rawVal);
                                lock (list)
                                {
                                    list.Add(new AutorunItem
                                    {
                                        Id = @"SafeBoot\AlternateShell",
                                        EntryName = "AlternateShell",
                                        ImagePath = clean,
                                        Arguments = ExtractArguments(rawVal, clean),
                                        RegistryLocation = @"HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot",
                                        RegistryHive = "HKLM",
                                        RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\SafeBoot",
                                        ValueName = "AlternateShell",
                                        Category = AutorunCategory.Logon,
                                        IsEnabled = true
                                    });
                                }
                            }
                        }
                    }

                    // Disabled AlternateShell
                    using (var disKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\AutorunsDisabled"))
                    {
                        if (disKey != null)
                        {
                            object val = disKey.GetValue("AlternateShell");
                            if (val != null)
                            {
                                string rawVal = val.ToString();
                                string clean = CleanImagePath(rawVal);
                                lock (list)
                                {
                                    list.Add(new AutorunItem
                                    {
                                        Id = @"SafeBoot\AutorunsDisabled\AlternateShell",
                                        EntryName = "AlternateShell",
                                        ImagePath = clean,
                                        Arguments = ExtractArguments(rawVal, clean),
                                        RegistryLocation = @"HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\AutorunsDisabled",
                                        RegistryHive = "HKLM",
                                        RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\SafeBoot\AutorunsDisabled",
                                        ValueName = "AlternateShell",
                                        Category = AutorunCategory.Logon,
                                        IsEnabled = false
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanActiveSetup(List<AutorunItem> list)
        {
            var paths = new[]
            {
                new { Path = @"SOFTWARE\Microsoft\Active Setup\Installed Components", View = RegistryView.Registry64 },
                new { Path = @"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components", View = RegistryView.Registry32 }
            };

            foreach (var p in paths)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, p.View))
                    using (var key = baseKey.OpenSubKey(p.Path))
                    {
                        if (key == null) continue;
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sub = key.OpenSubKey(subName))
                                {
                                    if (sub == null) continue;
                                    object stub = sub.GetValue("StubPath");
                                    if (stub == null) continue;

                                    string rawStub = stub.ToString();
                                    string clean = CleanImagePath(rawStub);
                                    if (string.IsNullOrWhiteSpace(clean)) continue;

                                    string compName = sub.GetValue("") as string;
                                    if (string.IsNullOrWhiteSpace(compName)) compName = subName;

                                    lock (list)
                                    {
                                        list.Add(new AutorunItem
                                        {
                                            Id = "ActiveSetup:" + subName,
                                            EntryName = compName,
                                            ImagePath = clean,
                                            Arguments = ExtractArguments(rawStub, clean),
                                            RegistryLocation = @"HKLM\" + p.Path + "\\" + subName,
                                            RegistryHive = "HKLM",
                                            RegistrySubKey = p.Path + "\\" + subName,
                                            ValueName = "StubPath",
                                            Category = AutorunCategory.Logon,
                                            IsEnabled = true
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
        }

        private void ScanWinlogon(List<AutorunItem> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                {
                    if (key == null) return;
                    string[] valNames = { "Userinit", "Shell", "Taskman" };
                    foreach (var v in valNames)
                    {
                        object val = key.GetValue(v);
                        if (val == null) continue;
                        string raw = val.ToString().Trim();
                        if (string.IsNullOrEmpty(raw)) continue;

                        string[] parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            string clean = CleanImagePath(part);
                            if (string.IsNullOrWhiteSpace(clean)) continue;
                            lock (list)
                            {
                                list.Add(new AutorunItem
                                {
                                    Id = "Winlogon:" + v + ":" + clean,
                                    EntryName = v,
                                    ImagePath = clean,
                                    RegistryLocation = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                                    RegistryHive = "HKLM",
                                    RegistrySubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                                    ValueName = v,
                                    Category = AutorunCategory.Logon,
                                    IsEnabled = true
                                });
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanStartupFolders(List<AutorunItem> list)
        {
            var folders = new[]
            {
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.Startup), Location = "User Startup Folder" },
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Location = "Common Startup Folder" }
            };

            foreach (var f in folders)
            {
                if (string.IsNullOrEmpty(f.Path) || !Directory.Exists(f.Path)) continue;

                try
                {
                    var dir = new DirectoryInfo(f.Path);
                    foreach (var file in dir.EnumerateFiles())
                    {
                        string ext = file.Extension.ToLowerInvariant();
                        if (ext != ".lnk" && ext != ".disabled") continue;

                        bool isEnabled = ext == ".lnk";
                        string targetPath = file.FullName;

                        if (ext == ".lnk")
                        {
                            string resolved = ResolveShortcutTarget(file.FullName);
                            if (!string.IsNullOrEmpty(resolved)) targetPath = resolved;

                            // Check StartupApproved in HKCU & HKLM
                            try
                            {
                                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
                                using (var appKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))
                                {
                                    if (appKey != null)
                                    {
                                        byte[] b = appKey.GetValue(file.Name) as byte[];
                                        if (b != null && b.Length > 0 && b[0] != 0x02) isEnabled = false;
                                    }
                                }
                            }
                            catch { }

                            try
                            {
                                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                                using (var appKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))
                                {
                                    if (appKey != null)
                                    {
                                        byte[] b = appKey.GetValue(file.Name) as byte[];
                                        if (b != null && b.Length > 0 && b[0] != 0x02) isEnabled = false;
                                    }
                                }
                            }
                            catch { }
                        }

                        var item = new AutorunItem
                        {
                            Id = file.FullName,
                            EntryName = Path.GetFileNameWithoutExtension(file.Name).Replace(".lnk", ""),
                            ImagePath = targetPath,
                            ShortcutPath = file.FullName,
                            RegistryLocation = f.Location,
                            Category = AutorunCategory.Logon,
                            IsEnabled = isEnabled
                        };

                        lock (list)
                        {
                            list.Add(item);
                        }
                    }
                }
                catch { }
            }
        }

        private void ScanScheduledTasks(List<AutorunItem> list)
        {
            string tasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
            if (!Directory.Exists(tasksDir)) return;

            try
            {
                var files = Directory.GetFiles(tasksDir, "*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    try
                    {
                        var doc = new XmlDocument();
                        doc.Load(file);

                        var execNodes = doc.GetElementsByTagName("Exec");
                        if (execNodes == null || execNodes.Count == 0) continue;

                        var execElem = execNodes[0] as XmlElement;
                        if (execElem == null) continue;

                        var cmdElem = execElem["Command"];
                        if (cmdElem == null || string.IsNullOrWhiteSpace(cmdElem.InnerText)) continue;

                        string rawCmd = cmdElem.InnerText.Trim();
                        string args = execElem["Arguments"] != null ? execElem["Arguments"].InnerText.Trim() : "";

                        string clean = CleanImagePath(rawCmd);

                        string desc = "";
                        var taskNode = doc["Task"];
                        var regInfo = taskNode != null ? taskNode["RegistrationInfo"] : null;
                        if (regInfo != null)
                        {
                            if (regInfo["Description"] != null) desc = regInfo["Description"].InnerText.Trim();
                        }

                        string author = "";
                        if (regInfo != null && regInfo["Author"] != null)
                        {
                            author = regInfo["Author"].InnerText.Trim();
                        }

                        bool isEnabled = true;
                        var settings = taskNode != null ? taskNode["Settings"] : null;
                        if (settings != null && settings["Enabled"] != null)
                        {
                            string enVal = settings["Enabled"].InnerText.Trim();
                            if (string.Equals(enVal, "false", StringComparison.OrdinalIgnoreCase))
                                isEnabled = false;
                        }

                        string relPath = file.Substring(tasksDir.Length);
                        string taskPath = "\\" + relPath.TrimStart('\\');
                        string taskName = Path.GetFileName(file);
                        bool isMsTask = taskPath.IndexOf(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        clean.IndexOf(@"\Program Files", StringComparison.OrdinalIgnoreCase) < 0;

                        var item = new AutorunItem
                        {
                            Id = "Task:" + taskPath,
                            EntryName = taskName,
                            Description = desc,
                            Publisher = author,
                            ImagePath = clean,
                            Arguments = args,
                            TaskPath = taskPath,
                            RegistryLocation = "Task Scheduler: " + Path.GetDirectoryName(taskPath),
                            Category = AutorunCategory.Task,
                            IsEnabled = isEnabled,
                            IsMicrosoft = isMsTask
                        };

                        lock (list)
                        {
                            list.Add(item);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ScanServicesAndDrivers(List<AutorunItem> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (key == null) return;

                    foreach (var subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sKey = key.OpenSubKey(subName))
                            {
                                if (sKey == null) continue;

                                object imgVal = sKey.GetValue("ImagePath");
                                if (imgVal == null) continue;

                                string rawImg = imgVal.ToString();
                                string cleanImg = CleanImagePath(rawImg);
                                if (string.IsNullOrWhiteSpace(cleanImg)) continue;

                                int startType = 3;
                                object startObj = sKey.GetValue("Start");
                                if (startObj != null) int.TryParse(startObj.ToString(), out startType);

                                int svcType = 16;
                                object typeObj = sKey.GetValue("Type");
                                if (typeObj != null) int.TryParse(typeObj.ToString(), out svcType);

                                bool isDriver = (svcType & 1) != 0 || (svcType & 2) != 0;
                                bool isEnabled = startType != 4; // 4 = Disabled

                                // Only keep services that are Automatic (2) or active Drivers or non-system services
                                if (!isDriver && startType != 2 && startType != 4)
                                {
                                    // If Manual, only keep if it's outside System32 to reduce clutter
                                    if (cleanImg.IndexOf(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) >= 0)
                                        continue;
                                }

                                string dispName = sKey.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(dispName) || dispName.StartsWith("@"))
                                    dispName = subName;

                                string desc = sKey.GetValue("Description") as string ?? "";
                                if (desc.StartsWith("@")) desc = "";

                                string entryTitle = isDriver ? dispName : subName;
                                string entryDesc = !string.IsNullOrWhiteSpace(desc) ? desc : (dispName != subName ? dispName : "");

                                var item = new AutorunItem
                                {
                                    Id = "Service:" + subName,
                                    EntryName = entryTitle,
                                    Description = entryDesc,
                                    ImagePath = cleanImg,
                                    ServiceName = subName,
                                    RegistryLocation = @"HKLM\SYSTEM\CurrentControlSet\Services\" + subName,
                                    Category = isDriver ? AutorunCategory.Driver : AutorunCategory.Service,
                                    IsEnabled = isEnabled
                                };

                                lock (list)
                                {
                                    list.Add(item);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void ScanExplorerExtensions(List<AutorunItem> list)
        {
            var roots = new[]
            {
                @"Directory\Background\shellex\ContextMenuHandlers",
                @"Directory\shellex\ContextMenuHandlers",
                @"*\shellex\ContextMenuHandlers"
            };

            foreach (var r in roots)
            {
                try
                {
                    using (var key = Registry.ClassesRoot.OpenSubKey(r))
                    {
                        if (key == null) continue;

                        foreach (var subName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var subKey = key.OpenSubKey(subName))
                                {
                                    if (subKey == null) continue;

                                    string clsid = subKey.GetValue("") as string;
                                    if (string.IsNullOrWhiteSpace(clsid) || !clsid.StartsWith("{"))
                                        continue;

                                    string dllPath = ResolveClsidDll(clsid);
                                    if (string.IsNullOrWhiteSpace(dllPath)) continue;

                                    var item = new AutorunItem
                                    {
                                        Id = "Explorer:" + r + "\\" + subName,
                                        EntryName = subName,
                                        ImagePath = dllPath,
                                        RegistryLocation = @"HKCR\" + r + "\\" + subName,
                                        RegistryHive = "HKCR",
                                        RegistrySubKey = r + "\\" + subName,
                                        ValueName = "",
                                        Category = AutorunCategory.Explorer,
                                        IsEnabled = true
                                    };

                                    lock (list)
                                    {
                                        list.Add(item);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        private void ScanCodecsAndDrivers32(List<AutorunItem> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                {
                    // Font Drivers (e.g. Adobe Type Manager -> atmfd.dll)
                    using (var fontKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Font Drivers"))
                    {
                        if (fontKey != null)
                        {
                            foreach (var v in fontKey.GetValueNames())
                            {
                                object val = fontKey.GetValue(v);
                                if (val == null) continue;
                                string clean = CleanImagePath(val.ToString());
                                lock (list)
                                {
                                    list.Add(new AutorunItem
                                    {
                                        Id = "FontDriver:" + v,
                                        EntryName = v,
                                        ImagePath = clean,
                                        RegistryLocation = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Font Drivers",
                                        RegistryHive = "HKLM",
                                        RegistrySubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Font Drivers",
                                        ValueName = v,
                                        Category = AutorunCategory.Driver,
                                        IsEnabled = true
                                    });
                                }
                            }
                        }
                    }

                    // Drivers32 (Codecs: MidisrvTransferComplete, msacm.platform, vidc.*, etc.)
                    using (var dKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32"))
                    {
                        if (dKey != null)
                        {
                            foreach (var v in dKey.GetValueNames())
                            {
                                object val = dKey.GetValue(v);
                                if (val == null) continue;
                                string raw = val.ToString();
                                string clean = CleanImagePath(raw);
                                lock (list)
                                {
                                    list.Add(new AutorunItem
                                    {
                                        Id = "Drivers32:" + v,
                                        EntryName = v,
                                        ImagePath = clean,
                                        RegistryLocation = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32",
                                        RegistryHive = "HKLM",
                                        RegistrySubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32",
                                        ValueName = v,
                                        Category = AutorunCategory.Driver,
                                        IsEnabled = true
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanKnownDLLs(List<AutorunItem> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs"))
                {
                    if (key == null) return;
                    string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string sysWowDir = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

                    foreach (var v in key.GetValueNames())
                    {
                        object val = key.GetValue(v);
                        if (val == null) continue;
                        string dll = val.ToString();
                        string path = Path.Combine(sysDir, dll);
                        if (!File.Exists(path) && Directory.Exists(sysWowDir))
                        {
                            string wowPath = Path.Combine(sysWowDir, dll);
                            if (File.Exists(wowPath)) path = wowPath;
                        }

                        lock (list)
                        {
                            list.Add(new AutorunItem
                            {
                                Id = "KnownDLL:" + v,
                                EntryName = v,
                                ImagePath = path,
                                RegistryLocation = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs",
                                RegistryHive = "HKLM",
                                RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs",
                                ValueName = v,
                                Category = AutorunCategory.Driver,
                                IsEnabled = true
                            });
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Enrichment & Helpers

        private void EnrichItems(List<AutorunItem> list)
        {
            Parallel.ForEach(list, item =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(item.ImagePath))
                    {
                        string clean = CleanImagePath(item.ImagePath);
                        item.FileExists = File.Exists(clean);

                        if (item.FileExists)
                        {
                            try
                            {
                                var ver = FileVersionInfo.GetVersionInfo(clean);

                                if (string.IsNullOrWhiteSpace(item.Description))
                                {
                                    if (!string.IsNullOrWhiteSpace(ver.FileDescription))
                                        item.Description = ver.FileDescription;
                                    else if (!string.IsNullOrWhiteSpace(ver.ProductName))
                                        item.Description = ver.ProductName;
                                }

                                if (string.IsNullOrWhiteSpace(item.Publisher) && !string.IsNullOrWhiteSpace(ver.CompanyName))
                                {
                                    item.Publisher = ver.CompanyName;
                                }

                                string signer;
                                bool verified = AuthenticodeHelper.CheckSignature(clean, out signer);
                                item.IsVerified = verified;
                                if (verified && !string.IsNullOrWhiteSpace(signer))
                                {
                                    item.SignerName = signer;
                                    if (string.IsNullOrWhiteSpace(item.Publisher))
                                    {
                                        item.Publisher = signer;
                                    }
                                }
                            }
                            catch { }

                            try
                            {
                                item.Icon = IconHelper.ExtractFromFile(clean, 0) ?? IconHelper.GetDefaultAppIcon();
                            }
                            catch { }

                            bool isAppDir = clean.IndexOf(@"\Program Files", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            clean.IndexOf(@"\AppData\", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (!string.IsNullOrWhiteSpace(item.Publisher) &&
                                (item.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 item.Publisher.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                if (isAppDir && (clean.IndexOf(@"\Microsoft\Edge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 clean.IndexOf(@"\EdgeUpdate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 clean.IndexOf(@"\OneDrive\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 clean.IndexOf(@"\Teams\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 clean.IndexOf(@"\Office", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    item.IsMicrosoft = false;
                                }
                                else
                                {
                                    item.IsMicrosoft = true;
                                }
                            }
                            else if (clean.IndexOf(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     clean.IndexOf(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                item.IsMicrosoft = true;
                            }
                        }
                        else
                        {
                            item.Icon = IconHelper.GetDefaultAppIcon();
                            item.IsMicrosoft = false;
                        }
                    }
                    else
                    {
                        item.Icon = IconHelper.GetDefaultAppIcon();
                    }
                }
                catch { }
            });
        }

        public static string CleanImagePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            if (s.StartsWith(@"\??\")) s = s.Substring(4);
            if (s.StartsWith("@")) s = s.Substring(1);

            try
            {
                s = Environment.ExpandEnvironmentVariables(s);
            }
            catch { }

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                s = Path.Combine(winDir, s.Substring(@"\SystemRoot\".Length));
            }
            else if (s.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                s = Path.Combine(winDir, s.Substring(@"SystemRoot\".Length));
            }
            else if (s.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"\system32\", StringComparison.OrdinalIgnoreCase))
            {
                string rel = s.TrimStart('\\');
                s = Path.Combine(winDir, rel);
            }

            int comma = s.LastIndexOf(',');
            if (comma > 0 && comma > s.LastIndexOf('.'))
            {
                s = s.Substring(0, comma);
            }

            if (s.StartsWith("\""))
            {
                int close = s.IndexOf('\"', 1);
                if (close > 1) s = s.Substring(1, close - 1);
                else s = s.Trim('\"');
            }
            else
            {
                string[] exts = new string[] { ".exe", ".dll", ".sys" };
                foreach (var ext in exts)
                {
                    int idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        s = s.Substring(0, idx + ext.Length);
                        break;
                    }
                }

                int spaceIdx = s.IndexOf(' ');
                if (spaceIdx > 0 && !File.Exists(s))
                {
                    string candidate = s.Substring(0, spaceIdx);
                    if (File.Exists(candidate)) s = candidate;
                }
            }

            s = s.Trim('\"', ' ');

            if (!Path.IsPathRooted(s))
            {
                string candidate = Path.Combine(winDir, "System32", s);
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(winDir, s);
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(winDir, "System32", "drivers", s);
                if (File.Exists(candidate)) return candidate;
            }

            return s;
        }

        private static string ExtractArguments(string raw, string clean)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(clean)) return "";
            string trimmedRaw = raw.Trim();
            if (trimmedRaw.StartsWith("\"" + clean + "\""))
            {
                return trimmedRaw.Substring(("\"" + clean + "\"").Length).Trim();
            }
            if (trimmedRaw.StartsWith(clean))
            {
                return trimmedRaw.Substring(clean.Length).Trim();
            }
            return "";
        }

        private static string ResolveClsidDll(string clsid)
        {
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32"))
                {
                    if (k != null)
                    {
                        string dll = k.GetValue("") as string;
                        if (!string.IsNullOrWhiteSpace(dll))
                            return CleanImagePath(dll);
                    }
                }
            }
            catch { }
            return "";
        }

        private static string ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                // Simple shell link resolver via COM
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(lnkPath);
                    string target = shortcut.TargetPath;
                    if (!string.IsNullOrWhiteSpace(target)) return target;
                }
            }
            catch { }
            return lnkPath;
        }

        #endregion

        #region Enable / Disable / Delete Operations

        public bool SetItemEnabled(AutorunItem item, bool enable, out string error)
        {
            error = null;
            if (item == null) return false;

            try
            {
                switch (item.Category)
                {
                    case AutorunCategory.Logon:
                        if (!string.IsNullOrEmpty(item.ShortcutPath))
                        {
                            return ToggleShortcut(item, enable, out error);
                        }
                        else
                        {
                            return ToggleRegistryRun(item, enable, out error);
                        }

                    case AutorunCategory.Task:
                        return ToggleScheduledTask(item, enable, out error);

                    case AutorunCategory.Service:
                    case AutorunCategory.Driver:
                        return ToggleService(item, enable, out error);

                    default:
                        error = "Данный тип автозапуска не поддерживает отключение";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool ToggleItem(AutorunItem item, out string error)
        {
            if (item == null)
            {
                error = "Item is null";
                return false;
            }
            return SetItemEnabled(item, !item.IsEnabled, out error);
        }

        private void UpdateStartupApprovedRun(RegistryHive hive, string normalSubKey, string valueName, bool enable)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                {
                    string appSub = normalSubKey.IndexOf("WOW6432Node", StringComparison.OrdinalIgnoreCase) >= 0
                        ? @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
                        : @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

                    using (var saKey = baseKey.OpenSubKey(appSub, true))
                    {
                        if (saKey != null)
                        {
                            byte[] b = saKey.GetValue(valueName) as byte[];
                            if (enable)
                            {
                                if (b != null && b.Length > 0)
                                {
                                    b[0] = 0x02; // Windows enabled flag
                                    saKey.SetValue(valueName, b, RegistryValueKind.Binary);
                                }
                            }
                            else
                            {
                                if (b != null && b.Length > 0)
                                {
                                    b[0] = 0x03; // Windows disabled flag
                                    saKey.SetValue(valueName, b, RegistryValueKind.Binary);
                                }
                                else
                                {
                                    byte[] dis = new byte[12];
                                    dis[0] = 0x03;
                                    saKey.SetValue(valueName, dis, RegistryValueKind.Binary);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void UpdateStartupApprovedFolder(string fileName, bool enable)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                    using (var saKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", true))
                    {
                        if (saKey != null)
                        {
                            byte[] b = saKey.GetValue(fileName) as byte[];
                            if (enable)
                            {
                                if (b != null && b.Length > 0)
                                {
                                    b[0] = 0x02;
                                    saKey.SetValue(fileName, b, RegistryValueKind.Binary);
                                }
                            }
                            else
                            {
                                if (b != null && b.Length > 0)
                                {
                                    b[0] = 0x03;
                                    saKey.SetValue(fileName, b, RegistryValueKind.Binary);
                                }
                                else
                                {
                                    byte[] dis = new byte[12];
                                    dis[0] = 0x03;
                                    saKey.SetValue(fileName, dis, RegistryValueKind.Binary);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private bool ToggleRegistryRun(AutorunItem item, bool enable, out string error)
        {
            error = null;
            try
            {
                var hive = item.RegistryHive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                {
                    string normalSubKey = item.RegistrySubKey.Replace(@"\AutorunsDisabled", "");
                    string disabledSubKey = normalSubKey + @"\AutorunsDisabled";

                    if (enable)
                    {
                        bool restored = false;

                        // Case A: Item was in AutorunsDisabled
                        using (var disKey = baseKey.OpenSubKey(disabledSubKey, true))
                        using (var normKey = baseKey.CreateSubKey(normalSubKey))
                        {
                            if (disKey != null && normKey != null)
                            {
                                object val = disKey.GetValue(item.ValueName);
                                if (val != null)
                                {
                                    normKey.SetValue(item.ValueName, val);
                                    disKey.DeleteValue(item.ValueName, false);
                                    restored = true;
                                }
                            }
                        }

                        // Case B: Item is already in normalSubKey (was disabled via StartupApproved in Task Manager)
                        if (!restored)
                        {
                            using (var normKey = baseKey.OpenSubKey(normalSubKey))
                            {
                                if (normKey != null && normKey.GetValue(item.ValueName) != null)
                                {
                                    restored = true;
                                }
                            }
                        }

                        if (restored)
                        {
                            // Reset StartupApproved in Windows Task Manager to enabled (0x02)
                            UpdateStartupApprovedRun(hive, normalSubKey, item.ValueName, true);
                            item.IsEnabled = true;
                            item.RegistrySubKey = normalSubKey;
                            item.RegistryLocation = item.RegistryHive + "\\" + normalSubKey;
                            return true;
                        }

                        error = "Не удалось найти запись реестра для включения";
                        return false;
                    }
                    else
                    {
                        // Disabling: move to AutorunsDisabled and set StartupApproved byte 0 = 0x03
                        using (var normKey = baseKey.OpenSubKey(normalSubKey, true))
                        using (var disKey = baseKey.CreateSubKey(disabledSubKey))
                        {
                            if (normKey != null && disKey != null)
                            {
                                object val = normKey.GetValue(item.ValueName);
                                if (val != null)
                                {
                                    disKey.SetValue(item.ValueName, val);
                                    normKey.DeleteValue(item.ValueName, false);
                                }
                            }
                        }

                        UpdateStartupApprovedRun(hive, normalSubKey, item.ValueName, false);
                        item.IsEnabled = false;
                        item.RegistrySubKey = disabledSubKey;
                        item.RegistryLocation = item.RegistryHive + "\\" + disabledSubKey;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        private bool ToggleShortcut(AutorunItem item, bool enable, out string error)
        {
            error = null;
            try
            {
                string originalFileName = Path.GetFileName(item.ShortcutPath);
                if (originalFileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    originalFileName = originalFileName.Substring(0, originalFileName.Length - ".disabled".Length);

                if (enable)
                {
                    if (item.ShortcutPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        string newPath = item.ShortcutPath.Substring(0, item.ShortcutPath.Length - ".disabled".Length);
                        File.Move(item.ShortcutPath, newPath);
                        item.ShortcutPath = newPath;
                    }
                    UpdateStartupApprovedFolder(originalFileName, true);
                    item.IsEnabled = true;
                    return true;
                }
                else
                {
                    if (item.ShortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        string newPath = item.ShortcutPath + ".disabled";
                        File.Move(item.ShortcutPath, newPath);
                        item.ShortcutPath = newPath;
                    }
                    UpdateStartupApprovedFolder(originalFileName, false);
                    item.IsEnabled = false;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        private bool ToggleScheduledTask(AutorunItem item, bool enable, out string error)
        {
            error = null;
            try
            {
                string flag = enable ? "/ENABLE" : "/DISABLE";
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = string.Format("/Change /TN \"{0}\" {1}", item.TaskPath, flag),
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
                            item.IsEnabled = enable;
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
            return false;
        }

        private bool ToggleService(AutorunItem item, bool enable, out string error)
        {
            error = null;
            try
            {
                string startVal = enable ? "auto" : "disabled";
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = string.Format("config \"{0}\" start= {1}", item.ServiceName, startVal),
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
                            item.IsEnabled = enable;
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
            return false;
        }

        public bool DeleteItem(AutorunItem item, out string error)
        {
            error = null;
            if (item == null) return false;

            try
            {
                switch (item.Category)
                {
                    case AutorunCategory.Logon:
                        if (!string.IsNullOrEmpty(item.ShortcutPath))
                        {
                            if (File.Exists(item.ShortcutPath)) File.Delete(item.ShortcutPath);
                            return true;
                        }
                        else
                        {
                            return DeleteRegistryRun(item, out error);
                        }

                    case AutorunCategory.Task:
                        return DeleteScheduledTask(item, out error);

                    case AutorunCategory.Service:
                    case AutorunCategory.Driver:
                        return DeleteService(item, out error);

                    case AutorunCategory.Explorer:
                        return DeleteExplorerExtension(item, out error);

                    default:
                        error = "Неподдерживаемый тип";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool DeleteRegistryRun(AutorunItem item, out string error)
        {
            error = null;
            try
            {
                var hive = item.RegistryHive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(item.RegistrySubKey, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(item.ValueName, false);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        private bool DeleteScheduledTask(AutorunItem item, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = string.Format("/Delete /TN \"{0}\" /F", item.TaskPath),
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
            return false;
        }

        private bool DeleteService(AutorunItem item, out string error)
        {
            error = null;
            if (item == null || string.IsNullOrWhiteSpace(item.ServiceName))
            {
                error = "Имя службы не указано";
                return false;
            }

            try
            {
                // 1. Force stop service / driver if active so Windows does not lock it in SERVICE_MARK_FOR_DELETE state
                try
                {
                    var stopPsi = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = string.Format("stop \"{0}\"", item.ServiceName),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    using (var sp = Process.Start(stopPsi))
                    {
                        if (sp != null) sp.WaitForExit(4000);
                    }
                }
                catch { }

                // 2. Delete service registration
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = string.Format("delete \"{0}\"", item.ServiceName),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(5000);
                        if (proc.ExitCode == 0) return true;
                        error = proc.StandardError.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        private bool DeleteExplorerExtension(AutorunItem item, out string error)
        {
            error = null;
            try
            {
                Registry.ClassesRoot.DeleteSubKeyTree(item.RegistrySubKey, false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        #endregion
    }
}
