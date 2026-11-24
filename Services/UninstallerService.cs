using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ApexUninstaller.Models;

namespace ApexUninstaller.Services
{
    public class UninstallerService
    {
        public async Task<bool> RunStandardUninstallAsync(InstalledApp app, bool silent = false)
        {
            if (app.IsUwp)
            {
                return await UninstallUwpPackageAsync(app.PackageFullName);
            }

            string cmd = silent && !string.IsNullOrWhiteSpace(app.QuietUninstallString)
                ? app.QuietUninstallString
                : app.UninstallString;

            if (string.IsNullOrWhiteSpace(cmd))
            {
                throw new InvalidOperationException(AppLang.Instance.IsRussian
                    ? "Строка деинсталляции для этой программы отсутствует в реестре."
                    : "Uninstall command for this application is not found in the registry.");
            }

            return await Task.Run(() =>
            {
                string exePath;
                string args;
                ParseCommand(cmd, out exePath, out args);

                // If MSI uninstall, ensure /X is used instead of /I
                if (exePath.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (args.IndexOf("/I", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        args = args.Replace("/I", "/X").Replace("/i", "/X");
                    }
                    else if (!args.IndexOf("/X", StringComparison.OrdinalIgnoreCase).Equals(0) &&
                             args.IndexOf("/x", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        args = "/X " + args;
                    }
                }

                // NSIS Uninstaller Handling:
                // NSIS uninstallers by default copy themselves to %TEMP% and exit the parent immediately.
                // Passing _?=<targetDir> forces NSIS to run synchronously in the foreground until user finishes!
                string targetDir = !string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation)
                    ? app.InstallLocation
                    : (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) ? Path.GetDirectoryName(exePath) : "");

                if (IsNsisExecutable(exePath))
                {
                    if (!string.IsNullOrEmpty(targetDir) && args.IndexOf("_?=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // NSIS syntax requirement: _?= must be the last parameter and MUST NOT contain quotes
                        string cleanTargetDir = targetDir.Trim().Trim('\"').TrimEnd('\\', '/');
                        args = (args + " _?=" + cleanTargetDir).Trim();
                    }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                DateTime startTime = DateTime.UtcNow;
                int processId = 0;

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        processId = proc.Id;
                        proc.WaitForExit();
                    }
                    else
                    {
                        return false;
                    }
                }

                // If process exited suspiciously fast (< 3 sec), monitor for any spawned uninstaller/child processes
                if ((DateTime.UtcNow - startTime).TotalSeconds < 3.0)
                {
                    WaitForUninstallerChildProcesses(targetDir);
                }

                // Give Windows a moment to release handles and flush file deletions
                System.Threading.Thread.Sleep(600);
                return true;
            });
        }

        public async Task<bool> UninstallUwpPackageAsync(string packageFullName)
        {
            if (string.IsNullOrWhiteSpace(packageFullName)) return false;

            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '{0}'\"", packageFullName),
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(30000);
                        return proc.ExitCode == 0;
                    }
                }
                return false;
            });
        }

        public static void ParseCommand(string commandLine, out string exePath, out string arguments)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                exePath = "";
                arguments = "";
                return;
            }

            commandLine = commandLine.Trim();

            // 1. Quoted executable path: "C:\Program Files\App\uninst.exe" /arg1 /arg2
            if (commandLine.StartsWith("\""))
            {
                int closeQuote = commandLine.IndexOf('\"', 1);
                if (closeQuote > 0)
                {
                    exePath = commandLine.Substring(1, closeQuote - 1).Trim();
                    arguments = commandLine.Substring(closeQuote + 1).Trim();
                    return;
                }
            }

            // 2. Unquoted path containing executable extensions (.exe, .bat, .cmd, .msi, .com)
            string[] extensions = new string[] { ".exe", ".bat", ".cmd", ".msi", ".com" };
            foreach (var ext in extensions)
            {
                int extIdx = 0;
                while ((extIdx = commandLine.IndexOf(ext, extIdx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int extEnd = extIdx + ext.Length;
                    // Verify boundary: either end of string or followed by space, quote, or parameter slash
                    if (extEnd == commandLine.Length || char.IsWhiteSpace(commandLine[extEnd]) || commandLine[extEnd] == '\"' || commandLine[extEnd] == '/')
                    {
                        string candidate = commandLine.Substring(0, extEnd).Trim('\"', ' ');
                        // If file exists or we hit end of string / whitespace separator, this is the executable
                        if (File.Exists(candidate) || extEnd == commandLine.Length || char.IsWhiteSpace(commandLine[extEnd]))
                        {
                            exePath = candidate;
                            arguments = commandLine.Substring(extEnd).Trim();
                            return;
                        }
                    }
                    extIdx = extEnd;
                }
            }

            // 3. Progressive check: check if prefix exists on disk (handling paths with spaces and no extension)
            int spaceIndex = commandLine.IndexOf(' ');
            while (spaceIndex > 0)
            {
                string candidate = commandLine.Substring(0, spaceIndex).Trim('\"', ' ');
                if (File.Exists(candidate))
                {
                    exePath = candidate;
                    arguments = commandLine.Substring(spaceIndex + 1).Trim();
                    return;
                }
                spaceIndex = commandLine.IndexOf(' ', spaceIndex + 1);
            }

            // 4. Fallback: split by first space if present
            int firstSpace = commandLine.IndexOf(' ');
            if (firstSpace > 0)
            {
                exePath = commandLine.Substring(0, firstSpace).Trim('\"', ' ');
                arguments = commandLine.Substring(firstSpace + 1).Trim();
                return;
            }

            exePath = commandLine.Trim('\"', ' ');
            arguments = "";
        }

        private static bool IsNsisExecutable(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

            try
            {
                string fileName = Path.GetFileName(exePath).ToLowerInvariant();
                if (fileName.Contains("uninst") || fileName.Contains("setup"))
                {
                    using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        byte[] buffer = new byte[Math.Min(fs.Length, 524288)];
                        int read = fs.Read(buffer, 0, buffer.Length);
                        string content = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                        if (content.IndexOf("Nullsoft", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static void WaitForUninstallerChildProcesses(string targetDir)
        {
            System.Threading.Thread.Sleep(800);
            string tempPath = Path.GetTempPath();
            int timeoutMs = 600000; // 10 minutes max
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool stillRunning = false;
                Process[] procs = null;
                try { procs = Process.GetProcesses(); } catch { break; }

                int myPid = Process.GetCurrentProcess().Id;

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == myPid || p.HasExited) continue;

                        string pName = p.ProcessName.ToLowerInvariant();
                        if (pName.StartsWith("au_") || pName.StartsWith("~nsu") || pName.Contains("uninst") || pName.Contains("setup"))
                        {
                            try
                            {
                                string filePath = p.MainModule != null ? p.MainModule.FileName : "";
                                if (!string.IsNullOrEmpty(filePath))
                                {
                                    if ((!string.IsNullOrEmpty(targetDir) && filePath.IndexOf(targetDir, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                        filePath.IndexOf("~nsu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        (filePath.IndexOf(tempPath, StringComparison.OrdinalIgnoreCase) >= 0 && (pName.StartsWith("au_") || pName.Contains("uninst"))))
                                    {
                                        stillRunning = true;
                                        p.WaitForExit(1000);
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (!stillRunning)
                {
                    System.Threading.Thread.Sleep(500);
                    break;
                }
            }
        }
    }
}
