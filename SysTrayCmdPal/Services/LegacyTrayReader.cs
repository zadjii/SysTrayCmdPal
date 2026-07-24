// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;
using SysTrayCmdPal.Interop;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Reads tray icons by walking Explorer's notification-area <c>ToolbarWindow32</c>
/// cross-process. Works on Windows 10 / the classic tray. Returns an empty list on
/// modern Windows 11 where the notification area is a XAML island (no toolbar).
/// </summary>
internal static class LegacyTrayReader
{
    private const uint RemoteBufferSize = 4096;

    /// <summary>
    /// Enumerate all tray icons. Each result carries <see cref="TrayIcon.IsVisible"/>
    /// so callers can present a "visible only" or "all icons" view.
    /// </summary>
    public static IReadOnlyList<TrayIcon> GetIcons()
    {
        var results = new List<TrayIcon>();
        HWND toolbar = FindNotificationToolbar();
        if (toolbar.IsNull)
        {
            return results;
        }

        ReadToolbar(toolbar, results);
        return results;
    }

    /// <summary>
    /// Locate the primary notification-area toolbar:
    /// <c>Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32</c>.
    /// This toolbar contains every icon; overflowed ones carry TBSTATE_HIDDEN.
    /// </summary>
    private static HWND FindNotificationToolbar()
    {
        HWND tray = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (tray.IsNull)
        {
            return HWND.Null;
        }

        HWND notify = PInvoke.FindWindowEx(tray, HWND.Null, "TrayNotifyWnd", null);
        if (notify.IsNull)
        {
            return HWND.Null;
        }

        HWND sysPager = PInvoke.FindWindowEx(notify, HWND.Null, "SysPager", null);
        HWND parent = sysPager.IsNull ? notify : sysPager;
        return PInvoke.FindWindowEx(parent, HWND.Null, "ToolbarWindow32", null);
    }

    private static unsafe void ReadToolbar(HWND toolbar, List<TrayIcon> results)
    {
        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(toolbar, &pid);
        if (pid == 0)
        {
            return;
        }

        HANDLE process = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE |
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION,
            false,
            pid);
        if (process.IsNull)
        {
            return;
        }

        void* remote = null;
        try
        {
            remote = PInvoke.VirtualAllocEx(
                process,
                null,
                RemoteBufferSize,
                VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE,
                PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
            if (remote == null)
            {
                return;
            }

            int count = (int)PInvoke.SendMessage(toolbar, PInvoke.TB_BUTTONCOUNT, default, default).Value;
            for (int i = 0; i < count; i++)
            {
                TrayIcon? icon = ReadButton(toolbar, process, remote, i);
                if (icon is not null)
                {
                    results.Add(icon);
                }
            }
        }
        finally
        {
            if (remote != null)
            {
                PInvoke.VirtualFreeEx(process, remote, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            }

            PInvoke.CloseHandle(process);
        }
    }

    private static unsafe TrayIcon? ReadButton(HWND toolbar, HANDLE process, void* remote, int index)
    {
        // Ask the toolbar to write the TBBUTTON for `index` into Explorer's buffer,
        // then copy it back into our address space.
        LRESULT got = PInvoke.SendMessage(toolbar, PInvoke.TB_GETBUTTON, (nuint)index, (nint)remote);
        if (got.Value == 0)
        {
            return null;
        }

        NativeTray.TBBUTTON64 button = default;
        if (!ReadRemote(process, remote, &button, (nuint)sizeof(NativeTray.TBBUTTON64)))
        {
            return null;
        }

        if (button.dwData == 0)
        {
            return null;
        }

        NativeTray.TRAYDATA data = default;
        if (!ReadRemote(process, (void*)button.dwData, &data, (nuint)sizeof(NativeTray.TRAYDATA)))
        {
            return null;
        }

        if (data.hwnd == 0)
        {
            return null;
        }

        uint ownerPid = 0;
        _ = PInvoke.GetWindowThreadProcessId((HWND)data.hwnd, &ownerPid);

        bool hidden = (button.fsState & NativeTray.TBSTATE_HIDDEN) != 0;

        return new TrayIcon
        {
            OwnerHwnd = data.hwnd,
            Id = data.uID,
            CallbackMessage = data.uCallbackMessage,
            IconHandle = data.hIcon,
            ProcessId = ownerPid,
            IsVisible = !hidden,
            Tooltip = ReadButtonText(toolbar, process, remote, button.idCommand),
            ProcessName = SafeProcessName(ownerPid),
        };
    }

    private static unsafe string ReadButtonText(HWND toolbar, HANDLE process, void* remote, int idCommand)
    {
        // Reuse the second half of the remote buffer for the text so it never
        // overlaps the TBBUTTON we may still be reading.
        const int TextOffset = 2048;
        void* remoteText = (byte*)remote + TextOffset;

        int len = (int)PInvoke.SendMessage(toolbar, PInvoke.TB_GETBUTTONTEXT, (nuint)idCommand, (nint)remoteText).Value;
        if (len <= 0)
        {
            return string.Empty;
        }

        int chars = Math.Min(len, (int)((RemoteBufferSize - TextOffset) / sizeof(char)) - 1);
        char[] buffer = new char[chars];
        fixed (char* local = buffer)
        {
            if (!ReadRemote(process, remoteText, local, (nuint)(chars * sizeof(char))))
            {
                return string.Empty;
            }
        }

        return new string(buffer).TrimEnd('\0');
    }

    private static unsafe bool ReadRemote(HANDLE process, void* source, void* destination, nuint size)
    {
        nuint read = 0;
        BOOL ok = PInvoke.ReadProcessMemory(process, source, destination, size, &read);
        return ok && read == size;
    }

    private static string SafeProcessName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
