using System;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Creates the PeekDesktop tray icon using raw Win32 GDI calls,
/// replacing the System.Drawing-based icon creation.
/// </summary>
internal static class Win32Icon
{
    /// <summary>
    /// Creates a 32×32 ARGB icon: blue monitor with a white "eye" symbol.
    /// Returns an HICON handle. Caller owns the handle and must call DestroyIcon.
    /// </summary>
    public static IntPtr CreateTrayIcon()
    {
        const int size = 32;
        int pixelCount = size * size;
        uint[] pixels = new uint[pixelCount];

        // Draw the icon into a pixel buffer (BGRA format, bottom-up DIB)
        DrawMonitorIcon(pixels, size);

        // Create a DIB section for the color bitmap
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        IntPtr hdc = GetDC(IntPtr.Zero);
        IntPtr hBitmap = CreateDIBSection(hdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, hdc);

        if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            return IntPtr.Zero;

        Marshal.Copy(MemoryMarshal.AsBytes<uint>(pixels).ToArray(), 0, bits, pixelCount * 4);

        // Monochrome mask (all zero = fully opaque, alpha channel does the work)
        IntPtr hMask = CreateBitmap(size, size, 1, 1, null);

        var iconInfo = new ICONINFO
        {
            fIcon = true,
            hbmMask = hMask,
            hbmColor = hBitmap
        };

        IntPtr hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hBitmap);
        DeleteObject(hMask);

        return hIcon;
    }

    private static void DrawMonitorIcon(uint[] pixels, int size)
    {
        const uint transparent = 0x00000000;
        const uint blue = 0xFF1E90FF;   // DodgerBlue
        const uint white = 0xFFFFFFFF;

        // Fill with transparent
        Array.Fill(pixels, transparent);

        // Monitor screen (filled blue rectangle)
        FillRect(pixels, size, 3, 3, 29, 21, blue);

        // Monitor bezel (white border)
        DrawRect(pixels, size, 3, 3, 29, 21, white);

        // Stand neck
        FillRect(pixels, size, 13, 22, 19, 25, white);

        // Stand base
        FillRect(pixels, size, 10, 25, 22, 27, white);

        // Eye on screen (simple ellipse approximation)
        // Outer eye shape
        SetPixel(pixels, size, 12, 10, white);
        SetPixel(pixels, size, 11, 11, white);
        SetPixel(pixels, size, 10, 12, white);
        SetPixel(pixels, size, 11, 13, white);
        SetPixel(pixels, size, 12, 14, white);
        SetPixel(pixels, size, 13, 15, white);
        for (int x = 14; x <= 18; x++) { SetPixel(pixels, size, x, 9, white); SetPixel(pixels, size, x, 15, white); }
        SetPixel(pixels, size, 19, 10, white);
        SetPixel(pixels, size, 20, 11, white);
        SetPixel(pixels, size, 21, 12, white);
        SetPixel(pixels, size, 20, 13, white);
        SetPixel(pixels, size, 19, 14, white);
        SetPixel(pixels, size, 13, 9, white);
        SetPixel(pixels, size, 18, 15, white);

        // Pupil (filled)
        FillRect(pixels, size, 15, 11, 18, 14, white);
    }

    private static void SetPixel(uint[] pixels, int stride, int x, int y, uint color)
    {
        if (x >= 0 && x < stride && y >= 0 && y < stride)
            pixels[y * stride + x] = color;
    }

    private static void FillRect(uint[] pixels, int stride, int x1, int y1, int x2, int y2, uint color)
    {
        for (int y = y1; y < y2; y++)
            for (int x = x1; x < x2; x++)
                SetPixel(pixels, stride, x, y, color);
    }

    private static void DrawRect(uint[] pixels, int stride, int x1, int y1, int x2, int y2, uint color)
    {
        for (int x = x1; x < x2; x++) { SetPixel(pixels, stride, x, y1, color); SetPixel(pixels, stride, x, y2 - 1, color); }
        for (int y = y1; y < y2; y++) { SetPixel(pixels, stride, x1, y, color); SetPixel(pixels, stride, x2 - 1, y, color); }
    }

    // --- P/Invoke ---

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
