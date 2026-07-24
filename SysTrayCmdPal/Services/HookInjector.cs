// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Injects <c>SysTrayHook.dll</c> into explorer.exe using
/// <c>SetWindowsHookEx(WH_GETMESSAGE)</c> and provides related shell helpers.
/// The DLL must match explorer's architecture; since this host process loads it
/// locally to resolve the hook proc, the host must also be that architecture.
/// </summary>
internal static partial class HookInjector
{
    private const int WH_GETMESSAGE = 3;
    private const uint WM_NULL = 0;
    private const nint HWND_BROADCAST = 0xffff;

    public readonly record struct Injection(nint Hook, uint ExplorerPid);

    /// <summary>Inject the DLL and return the hook handle, or null on failure.</summary>
    public static Injection? Inject(string dllPath)
    {
        nint hmod = LoadLibrary(dllPath);
        if (hmod == 0)
        {
            return null;
        }

        nint proc = GetProcAddress(hmod, "GetMsgProc");
        if (proc == 0)
        {
            return null;
        }

        nint tray = FindWindow("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return null;
        }

        uint tid = GetWindowThreadProcessId(tray, out uint pid);
        if (tid == 0)
        {
            return null;
        }

        nint hook = SetWindowsHookEx(WH_GETMESSAGE, proc, hmod, tid);
        if (hook == 0)
        {
            return null;
        }

        // Nudge explorer's tray thread so it dequeues a message and loads the DLL now.
        PostThreadMessage(tid, WM_NULL, 0, 0);
        return new Injection(hook, pid);
    }

    public static void Unhook(nint hook)
    {
        if (hook != 0)
        {
            UnhookWindowsHookEx(hook);
        }
    }

    public static uint GetExplorerPid()
    {
        nint tray = FindWindow("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(tray, out uint pid);
        return pid;
    }

    /// <summary>True when <paramref name="dllName"/> is already loaded in the process.</summary>
    public static bool IsInjected(uint explorerPid, string dllName)
    {
        try
        {
            using Process p = Process.GetProcessById((int)explorerPid);
            foreach (ProcessModule module in p.Modules)
            {
                if (string.Equals(module.ModuleName, dllName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Access can transiently fail; assume not injected so we (re)try.
        }

        return false;
    }

    /// <summary>Ask all apps to re-add their icons so an established hook captures them.</summary>
    public static void BroadcastTaskbarCreated()
    {
        uint msg = RegisterWindowMessage("TaskbarCreated");
        if (msg != 0)
        {
            SendNotifyMessage(HWND_BROADCAST, msg, 0, 0);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibrary(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetProcAddress(nint hModule, string name);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "SendNotifyMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SendNotifyMessage(nint hWnd, uint msg, nuint wParam, nint lParam);
}
