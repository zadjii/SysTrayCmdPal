// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace SysTrayHook;

/// <summary>
/// Hand-written, AOT-friendly P/Invokes and constants used by the injected hook.
/// Kept dependency-free so the published DLL stays minimal.
/// </summary>
internal static unsafe partial class Native
{
    // ---- Window messages / hook constants ----
    public const int GWLP_WNDPROC = -4;
    public const uint WM_COPYDATA = 0x004A;

    // Shell tray WM_COPYDATA discriminator (COPYDATASTRUCT.dwData).
    public const nuint SHELLTRAY_MAGIC = 1;

    // NIM_* messages carried inside SHELLTRAYDATA.
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;

    // GetDIBits / CreateDIBSection.
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
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

    // ---- user32 ----
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    // ---- gdi32 ----
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint h, int c, void* pv);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    public static partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, void* lpvBits, void* lpbmi, uint usage);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    // ---- kernel32 (named pipe client) ----
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NONE = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(nint hFile, void* lpBuffer, uint nNumberOfBytesToWrite, uint* lpNumberOfBytesWritten, nint lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    // GetModuleHandleEx flags for self-pinning.
    public const uint GET_MODULE_HANDLE_EX_FLAG_PIN = 0x1;
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint dwFlags, nint lpModuleName, out nint phModule);

    public static readonly nint INVALID_HANDLE_VALUE = new(-1);
}
