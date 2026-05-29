using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace LauncherGo.Ui.Platform;

internal static class WindowsDwmWindowEffects
{
    private const int DwmaNcRenderingPolicy = 2;
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmNcrpEnabled = 2;
    private const int DwmwcpRound = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var ncRenderingPolicy = DwmNcrpEnabled;
            _ = DwmSetWindowAttribute(hwnd, DwmaNcRenderingPolicy, ref ncRenderingPolicy, sizeof(int));

            // Ask DWM to render frame/shadow around custom-client windows.
            var margins = new Margins
            {
                LeftWidth = 1,
                RightWidth = 1,
                TopHeight = 1,
                BottomHeight = 1
            };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var cornerPreference = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref cornerPreference, sizeof(int));
            }

            _ = SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            // Best-effort DWM enhancement. Ignore on unsupported/locked environments.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr hwnd,
        ref Margins margins);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
