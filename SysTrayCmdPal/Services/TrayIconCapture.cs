// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Obtains a tray icon's image on Windows 11, where UI Automation exposes no
/// <c>HICON</c>. It screen-captures the icon button's on-screen bounding rectangle
/// (a centered square crop, so the surrounding button padding is discarded) and
/// caches the result as a PNG. This renders exactly what the user sees on the
/// taskbar, so every third-party and system icon comes through faithfully.
/// </summary>
internal static class TrayIconCapture
{
    // SRCCOPY | CAPTUREBLT: CAPTUREBLT is required to correctly grab the tray's
    // layered / XAML-island content instead of black or stale pixels.
    private const uint SrcCopyCaptureBlt = 0x00CC0020 | 0x40000000;

    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "SysTrayCmdPal", "icons");

    // Re-capture no more often than this, so dynamic icons (battery, volume,
    // network) refresh without re-grabbing the screen on every enumeration.
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// Capture the icon backing <paramref name="element"/> to a cached PNG and
    /// return its path, or null when the element has no usable on-screen rectangle.
    /// </summary>
    public static string? TryCaptureToFile(AutomationElement element, string key)
    {
        if (element is null)
        {
            return null;
        }

        if (Cache.TryGetValue(key, out string? cached)
            && File.Exists(cached)
            && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cached)) < Freshness)
        {
            return cached;
        }

        try
        {
            Rect rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
            {
                return null;
            }

            // Centered square crop isolates the glyph from the button's padding.
            int side = (int)Math.Floor(Math.Min(rect.Width, rect.Height));
            if (side < 1)
            {
                return null;
            }

            int srcX = (int)Math.Round(rect.X + ((rect.Width - side) / 2.0));
            int srcY = (int)Math.Round(rect.Y + ((rect.Height - side) / 2.0));

            byte[]? rgba = CaptureRect(srcX, srcY, side);
            if (rgba is null)
            {
                return null;
            }

            byte[] png = PngWriter.Encode(rgba, side, side);
            Directory.CreateDirectory(CacheDir);
            string path = Path.Combine(CacheDir, Sanitize(key) + ".png");
            File.WriteAllBytes(path, png);
            Cache[key] = path;
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe byte[]? CaptureRect(int srcX, int srcY, int side)
    {
        HDC screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            return null;
        }

        HDC mem = default;
        HBITMAP dib = default;
        try
        {
            mem = PInvoke.CreateCompatibleDC(screen);
            if (mem.IsNull)
            {
                return null;
            }

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = side;
            bmi.bmiHeader.biHeight = -side; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            void* bits = null;
            dib = PInvoke.CreateDIBSection(mem, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (dib.IsNull || bits == null)
            {
                return null;
            }

            HGDIOBJ old = PInvoke.SelectObject(mem, dib);
            BOOL copied = PInvoke.BitBlt(
                mem, 0, 0, side, side, screen, srcX, srcY, (ROP_CODE)SrcCopyCaptureBlt);
            _ = PInvoke.SelectObject(mem, old);

            if (!copied)
            {
                return null;
            }

            int pixelCount = side * side;
            byte[] rgba = new byte[pixelCount * 4];
            byte* src = (byte*)bits;

            for (int i = 0; i < pixelCount; i++)
            {
                byte b = src[(i * 4) + 0];
                byte g = src[(i * 4) + 1];
                byte r = src[(i * 4) + 2];

                rgba[(i * 4) + 0] = r;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = b;

                // BitBlt does not populate the alpha channel; the taskbar already
                // composited the icon, so treat every captured pixel as opaque.
                rgba[(i * 4) + 3] = 255;
            }

            return rgba;
        }
        finally
        {
            if (!dib.IsNull)
            {
                _ = PInvoke.DeleteObject(dib);
            }

            if (!mem.IsNull)
            {
                _ = PInvoke.DeleteDC(mem);
            }

            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    private static string Sanitize(string key)
    {
        Span<char> chars = stackalloc char[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '_';
        }

        return new string(chars);
    }
}
