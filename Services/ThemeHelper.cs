using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ApexUninstaller.Services
{
    public static class ThemeHelper
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        public static void ApplyDarkTitleBar(Window window)
        {
            if (window == null) return;

            window.SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                int darkMode = 1;
                // Enable dark mode for native titlebar (Win10 2004+ and Win11)
                if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref darkMode, sizeof(int));
                }

                // Match caption color to #131417 (BGR: 0x171413)
                int captionColor = 0x171413;
                DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                // White caption text
                int textColor = 0x00F0F0F0;
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            };
        }
    }
}
