using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ApiTester.App;

/// <summary>
/// Applies a dark window caption (title bar) via the Desktop Window Manager so the
/// OS-drawn chrome matches the Terminal Workbench theme. No-ops on Windows versions
/// that don't support these attributes (pre-Windows 11), leaving the default caption.
/// </summary>
internal static class NativeTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // COLORREF is 0x00BBGGRR.
    private static int Rgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            int caption = Rgb(0x0e, 0x12, 0x14); // Bg.Panel
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

            int text = Rgb(0xdc, 0xe4, 0xdf);    // Text.Normal
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));

            int border = Rgb(0x2a, 0x36, 0x3d);  // Border
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch
        {
            // Attribute unsupported on this OS build — keep the default caption.
        }
    }
}
