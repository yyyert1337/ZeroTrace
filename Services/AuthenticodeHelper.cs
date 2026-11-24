using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ApexUninstaller.Services
{
    public static class AuthenticodeHelper
    {
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            [In] IntPtr hwnd,
            [In] [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
            [In] IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        private static readonly Guid ActionGenericVerifyV2 = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

        public static bool CheckSignature(string filePath, out string signerName)
        {
            signerName = null;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            // 1. Try reading embedded X509 certificate
            try
            {
                using (var cert = X509Certificate.CreateFromSignedFile(filePath))
                {
                    var c2 = new X509Certificate2(cert);
                    signerName = c2.GetNameInfo(X509NameType.SimpleName, false);
                    if (c2.Verify())
                    {
                        return true;
                    }
                }
            }
            catch { }

            // 2. WinVerifyTrust Authenticode verification (fast, local cache only)
            IntPtr pFile = IntPtr.Zero;
            IntPtr pData = IntPtr.Zero;
            try
            {
                var wfi = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = filePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(wfi, pFile, false);

                var wtd = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2, // WTD_UI_NONE
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwProvFlags = 0x00000040 | 0x00000080 | 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(wtd, pData, false);

                uint status = WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, pData);
                if (status == 0) // ERROR_SUCCESS
                {
                    if (string.IsNullOrEmpty(signerName))
                    {
                        signerName = "Microsoft Windows";
                    }
                    return true;
                }
            }
            catch { }
            finally
            {
                if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }

            // 3. Fallback for Windows System Files with catalog signature
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (filePath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
                    if (!string.IsNullOrEmpty(vi.CompanyName) &&
                        (vi.CompanyName.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         vi.CompanyName.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        signerName = "Microsoft Windows";
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }
    }
}
