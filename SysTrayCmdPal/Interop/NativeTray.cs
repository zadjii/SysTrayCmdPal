// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace SysTrayCmdPal.Interop;

/// <summary>
/// Native layouts and constants used when reading the notification-area toolbar
/// out of the Explorer process. Layouts match the 64-bit Explorer process.
/// </summary>
internal static class NativeTray
{
    /// <summary>Toolbar button state: the button is hidden (icon lives in the overflow flyout).</summary>
    public const byte TBSTATE_HIDDEN = 0x08;

    /// <summary>Broadcast target for the <c>TaskbarCreated</c> refresh message.</summary>
    public static readonly nint HWND_BROADCAST = 0xFFFF;

    /// <summary>
    /// TBBUTTON as laid out in a 64-bit process. <c>dwData</c> points at a
    /// <see cref="TRAYDATA"/> inside the Explorer address space.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TBBUTTON64
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public byte bReserved0;
        public byte bReserved1;
        public byte bReserved2;
        public byte bReserved3;
        public byte bReserved4;
        public byte bReserved5;
        public nuint dwData;
        public nint iString;
    }

    /// <summary>
    /// The per-icon data Explorer stores behind each tray toolbar button
    /// (classic "TRAYDATA" layout).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TRAYDATA
    {
        public nint hwnd;
        public uint uID;
        public uint uCallbackMessage;
        public uint dwReserved0;
        public uint dwReserved1;
        public nint hIcon;
    }
}
