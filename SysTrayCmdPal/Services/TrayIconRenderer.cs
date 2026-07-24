// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Renders a tray icon's <c>HICON</c> to a cached PNG on disk and returns the path,
/// so it can be handed to the Command Palette <c>IconInfo(string)</c> constructor.
/// </summary>
internal static class TrayIconRenderer
{
    private const int IconSize = 32;

    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "SysTrayCmdPal", "icons");

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// Return a cached PNG path for <paramref name="iconHandle"/>, or null when the
    /// handle can't be rendered. <paramref name="key"/> uniquely identifies the icon
    /// content (owner + id + handle) so stale bitmaps are regenerated.
    /// </summary>
    public static string? TryRenderToFile(nint iconHandle, string key)
    {
        if (iconHandle == 0)
        {
            return null;
        }

        if (Cache.TryGetValue(key, out string? existing) && File.Exists(existing))
        {
            return existing;
        }

        try
        {
            byte[]? rgba = RenderToRgba(iconHandle, IconSize);
            if (rgba is null)
            {
                return null;
            }

            byte[] png = PngWriter.Encode(rgba, IconSize, IconSize);
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

    private static unsafe byte[]? RenderToRgba(nint iconHandle, int size)
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
            bmi.bmiHeader.biWidth = size;
            bmi.bmiHeader.biHeight = -size; // top-down
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
            BOOL drawn = PInvoke.DrawIconEx(
                mem, 0, 0, (HICON)iconHandle, size, size, 0, default, DI_FLAGS.DI_NORMAL);
            _ = PInvoke.SelectObject(mem, old);

            if (!drawn)
            {
                return null;
            }

            int pixelCount = size * size;
            byte[] rgba = new byte[pixelCount * 4];
            byte* src = (byte*)bits;
            bool anyAlpha = false;

            for (int i = 0; i < pixelCount; i++)
            {
                byte b = src[(i * 4) + 0];
                byte g = src[(i * 4) + 1];
                byte r = src[(i * 4) + 2];
                byte a = src[(i * 4) + 3];
                anyAlpha |= a != 0;

                rgba[(i * 4) + 0] = r;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = b;
                rgba[(i * 4) + 3] = a;
            }

            // Icons that only carry a mask (no alpha channel) come back fully
            // opaque-zero; treat them as opaque so they don't render invisible.
            if (!anyAlpha)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    rgba[(i * 4) + 3] = 255;
                }
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
