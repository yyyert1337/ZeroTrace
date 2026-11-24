using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace ApexUninstaller.Services
{
    public static class RegeditHelper
    {
        #region Win32 Privilege Elevation

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLengthInBytes, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static bool _privilegesEnabled = false;

        public static void EnableBackupAndTakeOwnershipPrivileges()
        {
            if (_privilegesEnabled) return;
            try
            {
                IntPtr hToken;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                {
                    try
                    {
                        string[] privs = new string[] { "SeTakeOwnershipPrivilege", "SeBackupPrivilege", "SeRestorePrivilege", "SeSecurityPrivilege" };
                        foreach (var priv in privs)
                        {
                            LUID luid;
                            if (LookupPrivilegeValue(null, priv, out luid))
                            {
                                var tp = new TOKEN_PRIVILEGES
                                {
                                    PrivilegeCount = 1,
                                    Privileges = new LUID_AND_ATTRIBUTES
                                    {
                                        Luid = luid,
                                        Attributes = SE_PRIVILEGE_ENABLED
                                    }
                                };
                                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        _privilegesEnabled = true;
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
            }
            catch { }
        }

        #endregion

        public static bool ForceDeleteRegistryTree(string hiveName, string subKeyPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(subKeyPath)) return true;

            var hive = hiveName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

            // 1. Standard deletion attempt
            try
            {
                if (hive == RegistryHive.LocalMachine)
                {
                    using (var b64 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    {
                        b64.DeleteSubKeyTree(subKeyPath, false);
                    }
                    using (var b32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32))
                    {
                        b32.DeleteSubKeyTree(subKeyPath, false);
                    }
                }
                else
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                    {
                        b.DeleteSubKeyTree(subKeyPath, false);
                    }
                }

                if (!KeyExists(hive, subKeyPath)) return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // 2. Take Ownership & ACL Override attempt
            try
            {
                EnableBackupAndTakeOwnershipPrivileges();
                TakeOwnershipAndGrantAccess(hive, subKeyPath);

                if (hive == RegistryHive.LocalMachine)
                {
                    using (var b64 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    {
                        b64.DeleteSubKeyTree(subKeyPath, false);
                    }
                    using (var b32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32))
                    {
                        b32.DeleteSubKeyTree(subKeyPath, false);
                    }
                }
                else
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                    {
                        b.DeleteSubKeyTree(subKeyPath, false);
                    }
                }

                if (!KeyExists(hive, subKeyPath)) return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // 3. reg.exe delete /f fallback
            try
            {
                string rootStr = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                if (hive == RegistryHive.LocalMachine)
                {
                    RunRegDelete(string.Format("delete \"{0}\\{1}\" /f /reg:64", rootStr, subKeyPath));
                    RunRegDelete(string.Format("delete \"{0}\\{1}\" /f /reg:32", rootStr, subKeyPath));
                }
                else
                {
                    RunRegDelete(string.Format("delete \"{0}\\{1}\" /f", rootStr, subKeyPath));
                }

                if (!KeyExists(hive, subKeyPath)) return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return !KeyExists(hive, subKeyPath);
        }

        private static void TakeOwnershipAndGrantAccess(RegistryHive hive, string subKeyPath)
        {
            var views = hive == RegistryHive.LocalMachine
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Default };

            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            foreach (var view in views)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership | RegistryRights.ChangePermissions))
                    {
                        if (key != null)
                        {
                            var sec = key.GetAccessControl();
                            sec.SetOwner(adminSid);
                            sec.AddAccessRule(new RegistryAccessRule(
                                adminSid,
                                RegistryRights.FullControl,
                                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                PropagationFlags.None,
                                AccessControlType.Allow));
                            key.SetAccessControl(sec);
                        }
                    }
                }
                catch { }
            }
        }

        public static bool KeyExists(RegistryHive hive, string subKeyPath)
        {
            try
            {
                if (hive == RegistryHive.LocalMachine)
                {
                    using (var b64 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    using (var k = b64.OpenSubKey(subKeyPath))
                    {
                        if (k != null) return true;
                    }
                    using (var b32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32))
                    using (var k = b32.OpenSubKey(subKeyPath))
                    {
                        if (k != null) return true;
                    }
                }
                else
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                    using (var k = b.OpenSubKey(subKeyPath))
                    {
                        if (k != null) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void RunRegDelete(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null) proc.WaitForExit(3000);
                }
            }
            catch { }
        }

        public static void OpenKeyInRegedit(string hiveName, string subKeyPath)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return;

            try
            {
                string fullHive = hiveName == "HKCU" ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

                // Dynamically detect localized root name ("Computer" / "Компьютер" / etc.)
                string rootName = "Computer";
                try
                {
                    using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    {
                        if (regKey != null)
                        {
                            string existing = regKey.GetValue("LastKey") as string;
                            if (!string.IsNullOrEmpty(existing))
                            {
                                int slash = existing.IndexOf('\\');
                                if (slash > 0)
                                {
                                    rootName = existing.Substring(0, slash);
                                }
                            }
                        }
                    }
                }
                catch { }

                if (rootName == "Computer" && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase))
                {
                    rootName = "Компьютер";
                }

                string fullPath = rootName + "\\" + fullHive + "\\" + subKeyPath;

                // Set LastKey in Regedit configuration
                using (var regKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                {
                    if (regKey != null)
                    {
                        regKey.SetValue("LastKey", fullPath);
                    }
                }

                // If regedit is already running, kill it so it picks up the new LastKey
                foreach (var p in Process.GetProcessesByName("regedit"))
                {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }

                Process.Start("regedit.exe");
            }
            catch
            {
                // Fallback to plain launch
                try { Process.Start("regedit.exe"); } catch { }
            }
        }
    }
}
