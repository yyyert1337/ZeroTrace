using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ApexUninstaller.Models;
using Microsoft.Win32;

namespace ApexUninstaller.Services
{
    public class InstalledAppsScanner
    {
        private readonly HashSet<string> _seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public async Task<List<InstalledApp>> ScanAllAsync(IProgress<int> progress)
        {
            return await Task.Run(() =>
            {
                var list = new List<InstalledApp>();
                _seenNames.Clear();

                // 1. Scan HKLM 64-bit
                ScanRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", AppArchitecture.X64, list);
                if (progress != null) progress.Report(30);

                // 2. Scan HKLM 32-bit (Wow6432Node)
                ScanRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", AppArchitecture.X86, list);
                if (progress != null) progress.Report(60);

                // 3. Scan HKCU
                ScanRegistryHive(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", AppArchitecture.X64, list);
                if (progress != null) progress.Report(80);

                // 4. Scan UWP / Windows Store Apps
                try
                {
                    ScanUwpApps(list);
                }
                catch
                {
                }
                if (progress != null) progress.Report(90);

                // 5. Resolve icons and calculate accurate disk sizes (including AppData & ProgramData)
                Parallel.ForEach(list, app =>
                {
                    try
                    {
                        CalculateAppTotalFootprint(app);
                    }
                    catch { }

                    try
                    {
                        app.Icon = IconHelper.GetIconForApp(app.DisplayIcon, app.InstallLocation, app.UninstallString, app.IsUwp);
                    }
                    catch { }
                });

                if (progress != null) progress.Report(100);

                return list.OrderBy(a => a.DisplayName).ToList();
            });
        }

        private void ScanRegistryHive(RegistryHive hive, RegistryView view, string subKeyPath, AppArchitecture defaultArch, List<InstalledApp> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstallKey = baseKey.OpenSubKey(subKeyPath))
                {
                    if (uninstallKey == null) return;

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (var appKey = uninstallKey.OpenSubKey(subKeyName))
                            {
                                if (appKey == null) continue;

                                string displayName = appKey.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(displayName)) continue;

                                displayName = displayName.Trim();

                                // Ignore System Components and Windows Updates
                                object systemComponent = appKey.GetValue("SystemComponent");
                                if (systemComponent != null && Convert.ToInt32(systemComponent) == 1)
                                    continue;

                                object parentKeyName = appKey.GetValue("ParentKeyName");
                                if (parentKeyName != null && !string.IsNullOrWhiteSpace(parentKeyName.ToString()))
                                    continue;

                                string releaseType = appKey.GetValue("ReleaseType") as string;
                                if (!string.IsNullOrEmpty(releaseType) && (releaseType.IndexOf("Security Update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    releaseType.IndexOf("Update Rollup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    releaseType.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0))
                                    continue;

                                // Filter duplicates based on unique DisplayName + Version
                                string displayVersion = appKey.GetValue("DisplayVersion") as string ?? "";
                                string dedupeKey = displayName + "|" + displayVersion;
                                lock (_seenNames)
                                {
                                    if (_seenNames.Contains(dedupeKey)) continue;
                                    _seenNames.Add(dedupeKey);
                                }

                                string uninstallString = appKey.GetValue("UninstallString") as string;
                                string quietUninstall = appKey.GetValue("QuietUninstallString") as string;
                                string rawLocation = appKey.GetValue("InstallLocation") as string;
                                string displayIcon = appKey.GetValue("DisplayIcon") as string;
                                string publisher = appKey.GetValue("Publisher") as string ?? "";

                                string installLocation = ResolveInstallLocation(appKey, rawLocation, displayIcon, uninstallString, displayName, publisher);

                                // Size in KB from EstimatedSize
                                long sizeBytes = 0;
                                object sizeObj = appKey.GetValue("EstimatedSize");
                                if (sizeObj != null)
                                {
                                    long kb;
                                    if (long.TryParse(sizeObj.ToString(), out kb))
                                    {
                                        sizeBytes = kb * 1024;
                                    }
                                }

                                // Install Date
                                DateTime? installDate = null;
                                string installDateStr = appKey.GetValue("InstallDate") as string;
                                if (!string.IsNullOrWhiteSpace(installDateStr) && installDateStr.Length == 8)
                                {
                                    DateTime dt;
                                    if (DateTime.TryParseExact(installDateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                                    {
                                        installDate = dt;
                                    }
                                }

                                var app = new InstalledApp
                                {
                                    Id = subKeyName,
                                    DisplayName = displayName,
                                    DisplayVersion = displayVersion,
                                    Publisher = publisher,
                                    InstallDate = installDate,
                                    InstallLocation = installLocation,
                                    UninstallString = uninstallString,
                                    QuietUninstallString = quietUninstall,
                                    DisplayIcon = displayIcon,
                                    RegistryKeyPath = subKeyPath + "\\" + subKeyName,
                                    RegistryHiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
                                    EstimatedSizeBytes = sizeBytes,
                                    Architecture = defaultArch,
                                    IsUwp = false
                                };

                                lock (list)
                                {
                                    list.Add(app);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void ScanUwpApps(List<InstalledApp> list)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | Select-Object Name, PackageFullName, Version, Publisher, InstallLocation | ConvertTo-Csv -NoTypeInformation\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        if (!proc.WaitForExit(7000))
                        {
                            try { proc.Kill(); } catch { }
                        }

                        using (var reader = new StringReader(output))
                        {
                            string header = reader.ReadLine();
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = ParseCsvLine(line);
                                if (parts.Length >= 4)
                                {
                                    string name = parts[0].Trim('\"');
                                    string fullName = parts[1].Trim('\"');
                                    string version = parts[2].Trim('\"');
                                    string publisher = parts[3].Trim('\"');
                                    string location = parts.Length > 4 ? parts[4].Trim('\"') : "";

                                    if (name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
                                        name.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase) ||
                                        name.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase) ||
                                        name.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    lock (_seenNames)
                                    {
                                        if (_seenNames.Contains(name)) continue;
                                        _seenNames.Add(name);
                                    }

                                    var uwpApp = new InstalledApp
                                    {
                                        Id = fullName,
                                        DisplayName = CleanPackageDisplayName(name),
                                        DisplayVersion = version,
                                        Publisher = CleanPublisher(publisher),
                                        InstallLocation = location,
                                        PackageFullName = fullName,
                                        IsUwp = true,
                                        Architecture = AppArchitecture.UWP,
                                        RegistryHiveName = "UWP",
                                        RegistryKeyPath = ""
                                    };

                                    lock (list)
                                    {
                                        list.Add(uwpApp);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            string current = "";

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current);
                    current = "";
                    continue;
                }
                current += c;
            }
            result.Add(current);
            return result.ToArray();
        }

        private static string CleanPackageDisplayName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "";
            if (rawName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                rawName = rawName.Substring(10);
            }
            rawName = rawName.Replace(".", " ");
            return Regex.Replace(rawName, @"\s+", " ").Trim();
        }

        private static string CleanPublisher(string pub)
        {
            if (string.IsNullOrWhiteSpace(pub)) return "";
            if (pub.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                int comma = pub.IndexOf(',');
                if (comma > 3) pub = pub.Substring(3, comma - 3);
                else pub = pub.Substring(3);
            }
            if (pub.StartsWith("{") || (pub.Length >= 32 && pub.Contains("-") && !pub.Contains(" ")))
            {
                return "";
            }
            return pub.Trim();
        }

        private static readonly HashSet<string> BlacklistedAppTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "microsoft", "system", "system32", "syswow64", "common files",
            "program files", "program files (x86)", "temp", "tmp", "desktop", "appdata",
            "local", "roaming", "locallow", "user", "users", "default", "public",
            "bin", "cache", "data", "logs", "config", "install", "installer", "setup",
            "helper", "package", "packages", "service", "services", "driver", "drivers",
            "programs", "crashdumps", "d3dscache", "comms", "connecteddevicesplatform",
            "softwaredistribution", "usoprivate", "usoshared", "peerdistrepub",
            "placeholdertilelogofolder", "npm-cache", "ssh", "system volume information",
            "$recycle.bin", "recovery", "intel", "amd", "nvidia", "google", "apple", "adobe"
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

        private static void CalculateAppTotalFootprint(InstalledApp app)
        {
            if (app == null) return;
            var countedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;

            // 1. Resolve primary install location
            string dirToMeasure = app.InstallLocation;
            if (string.IsNullOrWhiteSpace(dirToMeasure) || !Directory.Exists(dirToMeasure) || IsSystemFolder(dirToMeasure))
            {
                dirToMeasure = TryFindAppFolder(app);
                if (!string.IsNullOrWhiteSpace(dirToMeasure) && Directory.Exists(dirToMeasure) && !IsSystemFolder(dirToMeasure))
                {
                    app.InstallLocation = dirToMeasure;
                }
            }

            // 2. Measure primary install directory if valid
            if (!string.IsNullOrWhiteSpace(dirToMeasure) && Directory.Exists(dirToMeasure) && !IsSystemFolder(dirToMeasure))
            {
                try
                {
                    long realSize = FastDirectorySize(dirToMeasure, 30);
                    if (realSize > 0)
                    {
                        totalBytes = realSize;
                        countedPaths.Add(NormalizePath(dirToMeasure));
                    }
                }
                catch { }
            }

            // Fallback: if primary directory size was 0, preserve registry EstimatedSize if present
            if (totalBytes == 0 && app.EstimatedSizeBytes > 0)
            {
                totalBytes = app.EstimatedSizeBytes;
            }

            // 3. For UWP apps, find package data in AppData\Local\Packages
            if (app.IsUwp)
            {
                try
                {
                    MeasureUwpAppData(app, countedPaths, ref totalBytes);
                }
                catch { }
            }
            else
            {
                // 4. For Win32 apps, discover associated folders in AppData (Roaming, Local, LocalLow) and ProgramData
                try
                {
                    MeasureWin32AssociatedData(app, countedPaths, ref totalBytes);
                }
                catch { }
            }

            if (totalBytes > 0)
            {
                app.EstimatedSizeBytes = totalBytes;
            }
        }

        private static void MeasureUwpAppData(InstalledApp app, HashSet<string> countedPaths, ref long totalBytes)
        {
            if (string.IsNullOrWhiteSpace(app.PackageFullName)) return;

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string packagesDir = Path.Combine(localAppData, "Packages");
                if (!Directory.Exists(packagesDir)) return;

                int underIdx = app.PackageFullName.IndexOf('_');
                string pkgPrefix = underIdx > 0 ? app.PackageFullName.Substring(0, underIdx) : app.PackageFullName;

                var dirInfo = new DirectoryInfo(packagesDir);
                foreach (var pkgDir in dirInfo.EnumerateDirectories())
                {
                    if (pkgDir.Name.StartsWith(pkgPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsPathAlreadyCounted(pkgDir.FullName, countedPaths))
                        {
                            countedPaths.Add(NormalizePath(pkgDir.FullName));
                            long size = FastDirectorySize(pkgDir.FullName, 30);
                            totalBytes += size;
                        }
                    }
                }
            }
            catch { }
        }

        private static void MeasureWin32AssociatedData(InstalledApp app, HashSet<string> countedPaths, ref long totalBytes)
        {
            var tokens = ExtractSearchTokens(app);
            if (tokens.Count == 0) return;

            var baseDirs = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };

            foreach (var baseDir in baseDirs)
            {
                if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
                    continue;

                try
                {
                    var dirInfo = new DirectoryInfo(baseDir);
                    foreach (var sub in dirInfo.EnumerateDirectories())
                    {
                        string subName = sub.Name;

                        bool isContainer = IgnoredContainerDirs.Contains(subName);

                        if (!isContainer)
                        {
                            if (MatchesAnyToken(subName, tokens))
                            {
                                string fullPath = sub.FullName;
                                if (!IsSystemFolder(fullPath) && !IsPathAlreadyCounted(fullPath, countedPaths))
                                {
                                    countedPaths.Add(NormalizePath(fullPath));
                                    long folderSize = FastDirectorySize(fullPath, 30);
                                    totalBytes += folderSize;
                                }
                                continue;
                            }
                        }

                        // AppData\Local\Programs can contain user-installed app directories
                        if (subName.Equals("Programs", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                foreach (var progSub in sub.EnumerateDirectories())
                                {
                                    if (MatchesAnyToken(progSub.Name, tokens))
                                    {
                                        string progPath = progSub.FullName;
                                        if (!IsSystemFolder(progPath) && !IsPathAlreadyCounted(progPath, countedPaths))
                                        {
                                            countedPaths.Add(NormalizePath(progPath));
                                            long progSize = FastDirectorySize(progPath, 30);
                                            totalBytes += progSize;
                                        }
                                    }
                                }
                            }
                            catch { }
                            continue;
                        }

                        // Check publisher directory match: <Publisher>\<AppName>
                        if (!string.IsNullOrWhiteSpace(app.Publisher) &&
                            app.Publisher.Length >= 3 &&
                            !BlacklistedAppTokens.Contains(app.Publisher))
                        {
                            if (subName.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase) ||
                                (subName.IndexOf(app.Publisher, StringComparison.OrdinalIgnoreCase) >= 0 && subName.Length <= app.Publisher.Length + 10) ||
                                (app.Publisher.IndexOf(subName, StringComparison.OrdinalIgnoreCase) >= 0 && app.Publisher.Length <= subName.Length + 10))
                            {
                                try
                                {
                                    foreach (var inner in sub.EnumerateDirectories())
                                    {
                                        if (MatchesAnyToken(inner.Name, tokens))
                                        {
                                            string innerPath = inner.FullName;
                                            if (!IsSystemFolder(innerPath) && !IsPathAlreadyCounted(innerPath, countedPaths))
                                            {
                                                countedPaths.Add(NormalizePath(innerPath));
                                                long innerSize = FastDirectorySize(innerPath, 30);
                                                totalBytes += innerSize;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static bool MatchesAnyToken(string folderName, List<string> tokens)
        {
            if (string.IsNullOrWhiteSpace(folderName) || tokens == null || tokens.Count == 0)
                return false;

            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;

                if (folderName.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (token.Length >= 4)
                {
                    if (folderName.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
                        folderName.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) ||
                        folderName.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (token.Length >= 6)
                {
                    if (folderName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        folderName.Length <= token.Length + 4)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<string> ExtractSearchTokens(InstalledApp app)
        {
            var tokens = new List<string>();
            if (app == null) return tokens;

            string raw = app.DisplayName ?? "";
            string clean = Regex.Replace(raw, @"\([^)]*\)", " ");
            clean = Regex.Replace(clean, @"\b(?:version|v)?\d+[\d\.\-+_]*\b", " ", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\s+", " ").Trim();

            if (!string.IsNullOrWhiteSpace(clean) && clean.Length >= 3 && !BlacklistedAppTokens.Contains(clean))
            {
                tokens.Add(clean);
            }

            string compact = clean.Replace(" ", "");
            if (compact.Length >= 3 && !compact.Equals(clean, StringComparison.OrdinalIgnoreCase) && !BlacklistedAppTokens.Contains(compact))
            {
                if (!tokens.Contains(compact, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(compact);
                }
            }

            string[] genericSuffixes = new string[] { " browser", " launcher", " client", " desktop", " application", " app", " software", " suite", " tool", " tools" };
            foreach (var suf in genericSuffixes)
            {
                if (clean.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = clean.Substring(0, clean.Length - suf.Length).Trim();
                    if (baseName.Length >= 3 && !BlacklistedAppTokens.Contains(baseName) && !tokens.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                    {
                        tokens.Add(baseName);
                        string compactBase = baseName.Replace(" ", "");
                        if (compactBase.Length >= 3 && !tokens.Contains(compactBase, StringComparer.OrdinalIgnoreCase) && !BlacklistedAppTokens.Contains(compactBase))
                        {
                            tokens.Add(compactBase);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(app.Id) && !app.Id.StartsWith("{") && app.Id.Length >= 3 && !BlacklistedAppTokens.Contains(app.Id))
            {
                if (!tokens.Contains(app.Id, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(app.Id);
                }
            }

            return tokens;
        }

        private static bool IsPathAlreadyCounted(string path, HashSet<string> countedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || countedPaths == null || countedPaths.Count == 0)
                return false;

            string norm = NormalizePath(path);
            if (countedPaths.Contains(norm))
                return true;

            foreach (var cp in countedPaths)
            {
                if (norm.StartsWith(cp + "\\", StringComparison.OrdinalIgnoreCase) ||
                    cp.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            return p.Trim().TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static long FastDirectorySize(string path, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || maxDepth < 0) return 0;
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
                    if (depth > maxDepth) continue;

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
                                // Exclude nested game repositories (e.g. Steam\steamapps) so launcher size
                                // doesn't absorb the tens/hundreds of gigabytes of games already listed individually!
                                if (string.Equals(sub.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (string.Equals(sub.Name, "Games", StringComparison.OrdinalIgnoreCase) &&
                                    curDir.IndexOf("GOG Galaxy", StringComparison.OrdinalIgnoreCase) >= 0)
                                    continue;

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

        public static string TryFindAppFolder(InstalledApp app)
        {
            if (app == null) return null;
            return ResolveInstallLocation(null, app.InstallLocation, app.DisplayIcon, app.UninstallString, app.DisplayName, app.Publisher);
        }

        private static string ResolveInstallLocation(RegistryKey appKey, string rawLocation, string displayIcon, string uninstallString, string displayName, string publisher)
        {
            // 1. Direct clean-up of rawLocation
            string loc = CleanDirectoryPath(rawLocation);
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc) && !IsSystemFolder(loc))
                return loc;

            // 2. Alternative registry values in appKey
            if (appKey != null)
            {
                string[] candidateKeys = new string[] {
                    "Inno Setup: App Path",
                    "InstallDir",
                    "InstallDirectory",
                    "Path",
                    "TargetDir",
                    "Location",
                    "InstallSource",
                    "UninstallPath"
                };

                foreach (var k in candidateKeys)
                {
                    try
                    {
                        object val = appKey.GetValue(k);
                        if (val != null)
                        {
                            string candidate = CleanDirectoryPath(val.ToString());
                            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !IsSystemFolder(candidate))
                                return candidate;
                        }
                    }
                    catch { }
                }
            }

            // 3. Extract folder from DisplayIcon
            if (!string.IsNullOrWhiteSpace(displayIcon))
            {
                string iconDir = ExtractFolderFromPath(displayIcon);
                if (!string.IsNullOrEmpty(iconDir) && Directory.Exists(iconDir) && !IsSystemFolder(iconDir))
                    return iconDir;
            }

            // 4. Extract folder from UninstallString
            if (!string.IsNullOrWhiteSpace(uninstallString))
            {
                string uninstDir = ExtractFolderFromPath(uninstallString);
                if (!string.IsNullOrEmpty(uninstDir) && Directory.Exists(uninstDir) && !IsSystemFolder(uninstDir))
                    return uninstDir;
            }

            // 5. Look in App Paths (HKLM and HKCU)
            string appPathsDir = FindFromAppPaths(displayName);
            if (!string.IsNullOrEmpty(appPathsDir) && Directory.Exists(appPathsDir) && !IsSystemFolder(appPathsDir))
                return appPathsDir;

            // 6. Look in common program directories
            string commonDir = FindInCommonDirectories(displayName, publisher);
            if (!string.IsNullOrEmpty(commonDir) && Directory.Exists(commonDir))
                return commonDir;

            return loc ?? "";
        }

        private static string CleanDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('\"', '\'');
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (File.Exists(path))
            {
                try { return Path.GetDirectoryName(path); } catch { }
            }

            path = path.TrimEnd('\\', '/');
            return path;
        }

        private static string ExtractFolderFromPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();

            // Ignore MsiExec commands
            if (s.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            // Strip icon index suffix like ",0" or ",-1"
            int commaIdx = s.LastIndexOf(',');
            if (commaIdx > 0 && commaIdx > s.LastIndexOf('\\'))
            {
                s = s.Substring(0, commaIdx).Trim();
            }

            // If quoted: extract "C:\Path\foo.exe"
            if (s.StartsWith("\""))
            {
                int closeQuote = s.IndexOf('\"', 1);
                if (closeQuote > 1)
                {
                    s = s.Substring(1, closeQuote - 1);
                }
                else
                {
                    s = s.Trim('\"');
                }
            }
            else
            {
                int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0)
                {
                    s = s.Substring(0, exeIdx + 4);
                }
                else
                {
                    int spaceIdx = s.IndexOf(' ');
                    if (spaceIdx > 0 && !File.Exists(s) && !Directory.Exists(s))
                    {
                        s = s.Substring(0, spaceIdx);
                    }
                }
            }

            s = s.Trim('\"', '\'');

            if (File.Exists(s))
            {
                try { return Path.GetDirectoryName(s); } catch { }
            }

            if (Directory.Exists(s))
                return s;

            return null;
        }

        private static bool IsSystemFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string p = path.ToLowerInvariant();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System).ToLowerInvariant();
            string temp = Path.GetTempPath().ToLowerInvariant();

            if (p == win || p == sys || p.StartsWith(temp) || p.Contains("\\windows\\installer") || p.Contains("\\temp\\"))
                return true;

            if (p.Length <= 3 && p.Contains(":"))
                return true;

            return false;
        }

        private static string FindFromAppPaths(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;

            string cleanName = appName.Replace(" ", "").Replace("-", "").Replace("_", "");
            string[] searchNames = new string[] { appName + ".exe", cleanName + ".exe" };

            string[] hives = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
            };

            RegistryKey[] baseKeys = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (var hiveKey in baseKeys)
            {
                foreach (var h in hives)
                {
                    try
                    {
                        using (var sub = hiveKey.OpenSubKey(h))
                        {
                            if (sub == null) continue;
                            foreach (var s in searchNames)
                            {
                                using (var appSub = sub.OpenSubKey(s))
                                {
                                    if (appSub == null) continue;
                                    object pathVal = appSub.GetValue("Path") ?? appSub.GetValue("");
                                    if (pathVal != null)
                                    {
                                        string dir = CleanDirectoryPath(pathVal.ToString());
                                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !IsSystemFolder(dir))
                                            return dir;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string FindInCommonDirectories(string displayName, string publisher)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            string[] names = new string[] { displayName };
            int spaceIdx = displayName.IndexOf(' ');
            if (spaceIdx > 2)
            {
                names = new string[] { displayName, displayName.Substring(0, spaceIdx) };
            }

            string[] baseDirs = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            foreach (var baseDir in baseDirs)
            {
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;

                foreach (var name in names)
                {
                    string direct = Path.Combine(baseDir, name);
                    if (Directory.Exists(direct) && HasExecutable(direct))
                        return direct;

                    if (!string.IsNullOrWhiteSpace(publisher) && publisher.Length > 2)
                    {
                        string pubDir = Path.Combine(baseDir, publisher, name);
                        if (Directory.Exists(pubDir) && HasExecutable(pubDir))
                            return pubDir;
                    }
                }
            }

            return null;
        }

        private static bool HasExecutable(string folder)
        {
            try
            {
                return Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Length > 0;
            }
            catch { return false; }
        }
    }
}
