using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PokerAssistant.Vision;

public class ScreenCaptureService
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static IntPtr GetRootWindowFromPoint(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd != IntPtr.Zero)
        {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero) return root;
        }
        return hwnd;
    }

    public static string GetWindowDisplay(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "기본 바탕화면 (전체 화면)";
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        string title = sb.ToString();

        uint pid = 0;
        GetWindowThreadProcessId(hWnd, out pid);
        string procName = "";
        if (pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrEmpty(procName) ? $"창 (0x{hWnd:X})" : $"[{procName}] (0x{hWnd:X})";
        }
        return string.IsNullOrEmpty(procName) ? title : $"[{procName}] {title}";
    }

    public static List<(IntPtr Hwnd, string Title)> FindWindows(string keywordFilter)
    {
        var results = new List<(IntPtr Hwnd, string Title)>();
        var keywords = keywordFilter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowRect(hWnd, out RECT r);
            if (r.Width < 150 || r.Height < 100) return true;

            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            string procName = "";
            if (pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch { }
            }

            // Exclude common system background noise
            if (procName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("PokerAssistant", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();

            string display = GetWindowDisplay(hWnd);

            if (keywords.Length == 0)
            {
                results.Add((hWnd, display));
            }
            else
            {
                bool matched = false;
                foreach (var kw in keywords)
                {
                    if (procName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched)
                {
                    results.Add((hWnd, display));
                }
            }
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static bool GetWindowBounds(IntPtr hWnd, out RECT rect)
    {
        rect = default;
        if (hWnd == IntPtr.Zero) return false;
        int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
        if (hr == 0 && rect.Width > 0 && rect.Height > 0)
        {
            return true;
        }
        return GetWindowRect(hWnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    public static Bitmap? CaptureWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;

        if (!GetWindowBounds(hWnd, out RECT rect) || rect.Width <= 20 || rect.Height <= 20)
        {
            return null;
        }

        try
        {
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }
        catch
        {
            try
            {
                var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(hWnd, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    public static Bitmap CaptureDesktopRegion(Rectangle region)
    {
        int w = Math.Max(1, region.Width);
        int h = Math.Max(1, region.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
            }
        }
        catch
        {
            // Handle cases where desktop session is locked, non-interactive, or display DC is not available
        }
        return bmp;
    }

    public static Bitmap CropRegion(Bitmap source, float rx, float ry, float rw, float rh)
    {
        int x = Math.Max(0, (int)(source.Width * rx));
        int y = Math.Max(0, (int)(source.Height * ry));
        int w = Math.Min(source.Width - x, (int)(source.Width * rw));
        int h = Math.Min(source.Height - y, (int)(source.Height * rh));

        if (w <= 0 || h <= 0) return new Bitmap(10, 10);

        var rect = new Rectangle(x, y, w, h);
        return source.Clone(rect, source.PixelFormat);
    }
}
