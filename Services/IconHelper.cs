using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApexUninstaller.Services
{
    public static class IconHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SMALLICON = 0x000000001;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        public static ImageSource GetIconForApp(string displayIcon, string installLocation, string uninstallString, bool isUwp = false)
        {
            if (isUwp)
            {
                var uwpIcon = GetUwpAppIcon(installLocation);
                if (uwpIcon != null) return uwpIcon;
                return GetDefaultAppIcon();
            }

            try
            {
                // 1. Try displayIcon
                if (!string.IsNullOrWhiteSpace(displayIcon))
                {
                    string path = displayIcon.Trim('\"', ' ');
                    int iconIndex = 0;

                    int commaIdx = path.LastIndexOf(',');
                    if (commaIdx > 0)
                    {
                        string indexPart = path.Substring(commaIdx + 1).Trim();
                        int parsed;
                        if (int.TryParse(indexPart, out parsed))
                        {
                            iconIndex = parsed;
                            path = path.Substring(0, commaIdx).Trim('\"', ' ');
                        }
                    }

                    if (File.Exists(path))
                    {
                        var src = ExtractFromFile(path, iconIndex);
                        if (src != null) return src;
                    }
                }

                // 2. Try installLocation for an exe
                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                {
                    var files = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        string fn = Path.GetFileName(file).ToLowerInvariant();
                        if (fn.Contains("uninstall") || fn.Contains("unins") || fn.Contains("helper") || fn.Contains("update"))
                            continue;
                        var src = ExtractFromFile(file, 0);
                        if (src != null) return src;
                    }
                }

                // 3. Try parsing executable from uninstallString
                if (!string.IsNullOrWhiteSpace(uninstallString))
                {
                    string candidate = ExtractPathFromCommand(uninstallString);
                    if (File.Exists(candidate))
                    {
                        var src = ExtractFromFile(candidate, 0);
                        if (src != null) return src;
                    }
                }
            }
            catch
            {
                // Ignore extraction failures, return fallback
            }

            return GetDefaultAppIcon();
        }

        public static ImageSource GetUwpAppIcon(string installLocation)
        {
            if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
                return null;

            try
            {
                string assetsDir = Path.Combine(installLocation, "Assets");
                if (!Directory.Exists(assetsDir)) assetsDir = installLocation;

                var files = Directory.GetFiles(assetsDir, "*.png", SearchOption.AllDirectories);
                if (files == null || files.Length == 0) return null;

                string bestFile = null;
                // Priority 1: Square44x44 or SquareTile44x44
                foreach (var f in files)
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    if (fn.Contains("splash")) continue;
                    if (fn.Contains("44x44")) { bestFile = f; break; }
                }

                // Priority 2: Targetsize 32 or 48 or 24 or smalltile/logo
                if (bestFile == null)
                {
                    foreach (var f in files)
                    {
                        string fn = Path.GetFileName(f).ToLowerInvariant();
                        if (fn.Contains("splash")) continue;
                        if (fn.Contains("targetsize-32") || fn.Contains("targetsize-48") || fn.Contains("targetsize-24") || fn.Contains("smalltile") || fn.Contains("logo"))
                        {
                            bestFile = f;
                            break;
                        }
                    }
                }

                // Priority 3: any non-splash PNG
                if (bestFile == null)
                {
                    foreach (var f in files)
                    {
                        string fn = Path.GetFileName(f).ToLowerInvariant();
                        if (!fn.Contains("splash"))
                        {
                            bestFile = f;
                            break;
                        }
                    }
                }

                if (bestFile != null && File.Exists(bestFile))
                {
                    using (var stream = new FileStream(bestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = stream;
                        bi.EndInit();
                        bi.Freeze();
                        return bi;
                    }
                }
            }
            catch { }

            return null;
        }

        public static ImageSource ExtractFromFile(string filePath, int index)
        {
            try
            {
                if (filePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        return decoder.Frames[0];
                    }
                }

                IntPtr[] largeIcons = new IntPtr[1];
                IntPtr[] smallIcons = new IntPtr[1];
                uint count = ExtractIconEx(filePath, index, largeIcons, smallIcons, 1);
                IntPtr hIcon = IntPtr.Zero;

                if (count > 0 && largeIcons[0] != IntPtr.Zero)
                {
                    hIcon = largeIcons[0];
                }
                else if (count > 0 && smallIcons[0] != IntPtr.Zero)
                {
                    hIcon = smallIcons[0];
                }
                else
                {
                    // Fallback to SHGetFileInfo
                    SHFILEINFO shinfo = new SHFILEINFO();
                    IntPtr res = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                    if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        hIcon = shinfo.hIcon;
                    }
                }

                if (hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(
                            hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                        if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != hIcon)
                            DestroyIcon(smallIcons[0]);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ExtractPathFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int nextQuote = cmd.IndexOf('\"', 1);
                if (nextQuote > 1)
                {
                    return cmd.Substring(1, nextQuote - 1);
                }
            }
            int spaceIdx = cmd.IndexOf(' ');
            if (spaceIdx > 0)
            {
                return cmd.Substring(0, spaceIdx);
            }
            return cmd;
        }

        private static ImageSource _cachedDefault;
        public static ImageSource GetDefaultAppIcon()
        {
            if (_cachedDefault != null) return _cachedDefault;

            int width = 32;
            int height = 32;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 70, 229)); // Indigo
                var borderPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 102, 241)), 1.5);
                dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(2, 2, 28, 28), 6, 6);

                var whitePen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.8);
                dc.DrawRoundedRectangle(null, whitePen, new Rect(8, 8, 16, 16), 2, 2);
                dc.DrawLine(whitePen, new System.Windows.Point(8, 13), new System.Windows.Point(24, 13));
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(11, 10.5), 1, 1);
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(14, 10.5), 1, 1);
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            _cachedDefault = rtb;
            return _cachedDefault;
        }

        private static readonly Dictionary<string, ImageSource> _extensionIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> _fileSpecificIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        public static ImageSource GetFileSystemIcon(string fullPath, bool isDirectory)
        {
            if (string.IsNullOrEmpty(fullPath)) return GetDefaultAppIcon();

            // 1. Folders: universal folder icon
            if (isDirectory)
            {
                lock (_extensionIconCache)
                {
                    ImageSource cached;
                    if (_extensionIconCache.TryGetValue("::dir::", out cached)) return cached;
                }

                try
                {
                    SHFILEINFO shinfo = new SHFILEINFO();
                    SHGetFileInfo("dummy_dir", FILE_ATTRIBUTE_DIRECTORY, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
                    if (shinfo.hIcon != IntPtr.Zero)
                    {
                        var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        DestroyIcon(shinfo.hIcon);

                        lock (_extensionIconCache)
                        {
                            _extensionIconCache["::dir::"] = img;
                        }
                        return img;
                    }
                }
                catch { }

                return GetDefaultAppIcon();
            }

            // 2. Files: determine if this file has a unique custom embedded icon
            string ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "::noext::";

            bool isPerFileIcon = (ext == ".exe" || ext == ".lnk" || ext == ".ico" || ext == ".cur" || ext == ".ani" || ext == ".url");

            if (isPerFileIcon)
            {
                // Check per-file cache first
                lock (_fileSpecificIconCache)
                {
                    ImageSource cached;
                    if (_fileSpecificIconCache.TryGetValue(fullPath, out cached)) return cached;
                }

                // Extract specific icon from this file
                try
                {
                    SHFILEINFO shinfo = new SHFILEINFO();
                    // Without SHGFI_USEFILEATTRIBUTES: extracts the actual embedded icon of this specific exe/lnk
                    SHGetFileInfo(fullPath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON);
                    if (shinfo.hIcon != IntPtr.Zero)
                    {
                        var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        DestroyIcon(shinfo.hIcon);

                        lock (_fileSpecificIconCache)
                        {
                            if (_fileSpecificIconCache.Count > 3000) _fileSpecificIconCache.Clear();
                            _fileSpecificIconCache[fullPath] = img;
                        }
                        return img;
                    }
                }
                catch { }

                // Fallback for missing/deleted exe/lnk: will resolve generic extension icon below
            }

            // 3. Generic extension icon (cached by extension across all files)
            lock (_extensionIconCache)
            {
                ImageSource cached;
                if (_extensionIconCache.TryGetValue(ext, out cached)) return cached;
            }

            try
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                // Pass "dummy" + ext with SHGFI_USEFILEATTRIBUTES so it resolves the registered shell icon from memory without disk I/O
                string lookupName = (ext == "::noext::") ? "dummyfile" : ("dummy" + ext);
                SHGetFileInfo(lookupName, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    DestroyIcon(shinfo.hIcon);

                    lock (_extensionIconCache)
                    {
                        _extensionIconCache[ext] = img;
                    }
                    return img;
                }
            }
            catch { }

            return GetDefaultAppIcon();
        }
    }
}
