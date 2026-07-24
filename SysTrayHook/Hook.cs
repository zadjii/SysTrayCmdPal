// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysTrayHook;

/// <summary>
/// Code that runs <b>inside explorer.exe</b> after the DLL is injected via
/// <c>SetWindowsHookEx(WH_GETMESSAGE)</c>. It subclasses <c>Shell_TrayWnd</c>,
/// intercepts the <c>WM_COPYDATA</c> shell-tray notifications that apps send when
/// they add/modify/remove a notification-area icon, extracts the real icon pixels,
/// and streams everything to the host over a named pipe.
///
/// Every exported/callback entrypoint is fully guarded: an exception escaping an
/// <see cref="UnmanagedCallersOnly"/> method would crash the shell, so we never let
/// one out and we always chain to the original window procedure.
/// </summary>
internal static unsafe class Hook
{
    // Must match SysTrayCmdPal's HookPipe.PipeName.
    private const string PipeName = @"\\.\pipe\SysTrayCmdPal.systray";
    private const uint WireMagic = 0x53545243; // 'STRC'
    private const uint NIF_GUID = 0x20;

    // SHELLTRAYDATA / NOTIFYICONDATA (shell internal wire layout) field offsets.
    // Handles are stored as 32-bit even on 64-bit Windows.
    private const int OffMessageType = 4;
    private const int IconDataStart = 8;
    private const int OffHWnd = IconDataStart + 4;
    private const int OffUID = IconDataStart + 8;
    private const int OffUFlags = IconDataStart + 12;
    private const int OffCallback = IconDataStart + 16;
    private const int OffHIcon = IconDataStart + 20;
    private const int OffSzTip = IconDataStart + 24;   // 128 wchars
    private const int TipChars = 128;
    private const int OffVersion = IconDataStart + 800; // union uTimeout/uVersion
    private const int OffGuid = IconDataStart + 936;    // guidItem (16 bytes)
    private const int ShellTrayDataSize = IconDataStart + 956;

    private static int s_initialized;
    private static nint s_originalWndProc;
    private static nint s_pipe = Native.INVALID_HANDLE_VALUE;

    /// <summary>
    /// WH_GETMESSAGE hook procedure. Its only real job is to run one-time setup the
    /// first time explorer's tray thread dispatches a message, then get out of the way.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "GetMsgProc")]
    public static nint GetMsgProc(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (Interlocked.CompareExchange(ref s_initialized, 1, 0) == 0)
            {
                Initialize();
            }
        }
        catch
        {
            // Never let anything escape.
        }

        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private static void Initialize()
    {
        nint tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return;
        }

        // Pin ourselves in explorer: the subclass below installs a pointer into this
        // module, so the DLL must never unload (even after the injection hook is
        // removed) or the next tray message would jump into freed memory.
        Native.GetModuleHandleEx(
            Native.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Native.GET_MODULE_HANDLE_EX_FLAG_PIN,
            (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&SubclassProc,
            out _);

        nint self = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&SubclassProc;
        nint previous = Native.SetWindowLongPtr(tray, Native.GWLP_WNDPROC, self);
        if (previous != 0)
        {
            s_originalWndProc = previous;
        }
    }

    /// <summary>Replacement window procedure for <c>Shell_TrayWnd</c>.</summary>
    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == Native.WM_COPYDATA && lParam != 0)
        {
            try
            {
                HandleCopyData(lParam);
            }
            catch
            {
                // Swallow — must always fall through to the original proc.
            }
        }

        nint orig = s_originalWndProc;
        return orig != 0
            ? Native.CallWindowProc(orig, hWnd, msg, wParam, lParam)
            : hWnd; // Should never happen; avoid eating the message entirely.
    }

    private static void HandleCopyData(nint lParam)
    {
        ref Native.COPYDATASTRUCT cds = ref Unsafe.AsRef<Native.COPYDATASTRUCT>((void*)lParam);
        if (cds.dwData != Native.SHELLTRAY_MAGIC || cds.lpData == 0 || cds.cbData < ShellTrayDataSize)
        {
            return;
        }

        byte* p = (byte*)cds.lpData;
        uint nim = Read32(p, OffMessageType);
        if (nim != Native.NIM_ADD && nim != Native.NIM_MODIFY &&
            nim != Native.NIM_DELETE && nim != Native.NIM_SETVERSION)
        {
            return;
        }

        ulong hwnd = Read32(p, OffHWnd);
        uint uid = Read32(p, OffUID);
        uint uFlags = Read32(p, OffUFlags);
        uint callback = Read32(p, OffCallback);
        uint hIcon = Read32(p, OffHIcon);
        uint version = Read32(p, OffVersion);

        // Tooltip (NUL-terminated UTF-16, up to 128 chars).
        int tipLen = 0;
        char* tip = (char*)(p + OffSzTip);
        while (tipLen < TipChars && tip[tipLen] != '\0')
        {
            tipLen++;
        }

        byte[]? rgba = null;
        int w = 0, h = 0;
        if (nim != Native.NIM_DELETE && hIcon != 0)
        {
            rgba = TryExtractRgba(hIcon, out w, out h);
        }

        SendMessage(nim, hwnd, uid, uFlags, callback, version, (byte*)tip, tipLen * 2, p + OffGuid, rgba, w, h);
    }

    private static byte[]? TryExtractRgba(uint hIcon, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!Native.GetIconInfo((nint)hIcon, out Native.ICONINFO ii))
        {
            return null;
        }

        nint hbmColor = ii.hbmColor;
        nint hbmMask = ii.hbmMask;
        try
        {
            if (hbmColor == 0)
            {
                return null; // monochrome (mask-only) icon: skip.
            }

            Native.BITMAP bm;
            if (Native.GetObject(hbmColor, sizeof(Native.BITMAP), &bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            {
                return null;
            }

            int w = bm.bmWidth;
            int h = bm.bmHeight;
            width = w;
            height = h;

            var bmi = default(Native.BITMAPINFOHEADER);
            bmi.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
            bmi.biWidth = w;
            bmi.biHeight = -h; // top-down
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            bmi.biCompression = Native.BI_RGB;

            byte[] bgra = new byte[w * h * 4];
            nint hdc = Native.GetDC(0);
            if (hdc == 0)
            {
                return null;
            }

            int scan;
            fixed (byte* dst = bgra)
            {
                scan = Native.GetDIBits(hdc, hbmColor, 0, (uint)h, dst, &bmi, Native.DIB_RGB_COLORS);
            }

            Native.ReleaseDC(0, hdc);
            if (scan == 0)
            {
                return null;
            }

            // BGRA -> RGBA. Detect a missing alpha channel (older icons) and derive
            // opacity from the AND mask instead.
            bool anyAlpha = false;
            for (int i = 0; i < w * h; i++)
            {
                if (bgra[(i * 4) + 3] != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }

            byte[]? maskBits = null;
            if (!anyAlpha && hbmMask != 0)
            {
                maskBits = ReadMaskAlpha(hbmMask, w, h);
            }

            byte[] rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                byte b = bgra[(i * 4) + 0];
                byte g = bgra[(i * 4) + 1];
                byte r = bgra[(i * 4) + 2];
                byte a = bgra[(i * 4) + 3];
                if (!anyAlpha)
                {
                    a = maskBits is not null ? maskBits[i] : (byte)255;
                }

                rgba[(i * 4) + 0] = r;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = b;
                rgba[(i * 4) + 3] = a;
            }

            return rgba;
        }
        finally
        {
            if (hbmColor != 0)
            {
                Native.DeleteObject(hbmColor);
            }

            if (hbmMask != 0)
            {
                Native.DeleteObject(hbmMask);
            }
        }
    }

    // AND mask: bit set (1) => transparent, clear (0) => opaque.
    private static byte[]? ReadMaskAlpha(nint hbmMask, int w, int h)
    {
        var bmi = default(Native.BITMAPINFOHEADER);
        bmi.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
        bmi.biWidth = w;
        bmi.biHeight = -h;
        bmi.biPlanes = 1;
        bmi.biBitCount = 32;
        bmi.biCompression = Native.BI_RGB;

        byte[] tmp = new byte[w * h * 4];
        nint hdc = Native.GetDC(0);
        if (hdc == 0)
        {
            return null;
        }

        int scan;
        fixed (byte* dst = tmp)
        {
            scan = Native.GetDIBits(hdc, hbmMask, 0, (uint)h, dst, &bmi, Native.DIB_RGB_COLORS);
        }

        Native.ReleaseDC(0, hdc);
        if (scan == 0)
        {
            return null;
        }

        byte[] alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            // Mask drawn as 32bpp: white (nonzero) = transparent.
            alpha[i] = tmp[(i * 4) + 0] != 0 ? (byte)0 : (byte)255;
        }

        return alpha;
    }

    private static void SendMessage(
        uint nim, ulong hwnd, uint uid, uint uFlags, uint callback, uint version,
        byte* tip, int tipBytes, byte* guid, byte[]? rgba, int w, int h)
    {
        int iconBytes = rgba?.Length ?? 0;
        const int header = 68;
        int total = header + tipBytes + iconBytes;
        byte[] buf = new byte[total];

        fixed (byte* b = buf)
        {
            int o = 0;
            Write32(b, ref o, WireMagic);
            Write32(b, ref o, nim);
            Write64(b, ref o, hwnd);
            Write32(b, ref o, uid);
            Write32(b, ref o, uFlags);
            Write32(b, ref o, callback);
            Write32(b, ref o, version);
            Write32(b, ref o, (uFlags & NIF_GUID) != 0 ? 1u : 0u);
            Buffer.MemoryCopy(guid, b + o, 16, 16);
            o += 16;
            Write32(b, ref o, (uint)tipBytes);
            Write32(b, ref o, (uint)w);
            Write32(b, ref o, (uint)h);
            Write32(b, ref o, (uint)iconBytes);

            if (tipBytes > 0)
            {
                Buffer.MemoryCopy(tip, b + o, tipBytes, tipBytes);
                o += tipBytes;
            }

            if (iconBytes > 0)
            {
                fixed (byte* src = rgba)
                {
                    Buffer.MemoryCopy(src, b + o, iconBytes, iconBytes);
                }

                o += iconBytes;
            }

            WriteToPipe(b, total);
        }
    }

    private static void WriteToPipe(byte* data, int length)
    {
        if (!EnsurePipe())
        {
            return;
        }

        uint written;
        bool ok = Native.WriteFile(s_pipe, data, (uint)length, &written, 0);
        if (!ok)
        {
            // Server went away; drop the handle so we reconnect next time.
            Native.CloseHandle(s_pipe);
            s_pipe = Native.INVALID_HANDLE_VALUE;
        }
    }

    private static bool EnsurePipe()
    {
        if (s_pipe != Native.INVALID_HANDLE_VALUE)
        {
            return true;
        }

        nint h = Native.CreateFile(PipeName, Native.GENERIC_WRITE, 0, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_NONE, 0);
        if (h == Native.INVALID_HANDLE_VALUE)
        {
            return false;
        }

        s_pipe = h;
        return true;
    }

    private static uint Read32(byte* p, int offset) => Unsafe.ReadUnaligned<uint>(p + offset);

    private static void Write32(byte* p, ref int offset, uint value)
    {
        Unsafe.WriteUnaligned(p + offset, value);
        offset += 4;
    }

    private static void Write64(byte* p, ref int offset, ulong value)
    {
        Unsafe.WriteUnaligned(p + offset, value);
        offset += 8;
    }
}
