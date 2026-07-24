// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows.Automation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using SysTrayCmdPal.Interop;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Invokes a tray icon. For UI-Automation-sourced icons (Win11) it uses the Invoke
/// pattern for the default action and synthesizes real mouse clicks at the icon's
/// location for right/middle clicks. For legacy (Win10) icons it replays the mouse
/// messages the shell would post through the icon's callback message.
/// </summary>
internal static class TrayIconInvoker
{
    /// <summary>Left click / default action.</summary>
    public static void Activate(TrayIcon icon)
    {
        if (icon.Element is { } element)
        {
            if (!UiaTrayReader.TryInvoke(element))
            {
                SynthesizeClick(element, ClickKind.Left);
            }

            return;
        }

        SendLegacy(icon, PInvoke.WM_LBUTTONDOWN);
        SendLegacy(icon, PInvoke.WM_LBUTTONUP);
    }

    /// <summary>Right click — asks the owning app to show its context menu.</summary>
    public static void OpenContextMenu(TrayIcon icon)
    {
        if (icon.Element is { } element)
        {
            SynthesizeClick(element, ClickKind.Right);
            return;
        }

        SendLegacy(icon, PInvoke.WM_RBUTTONDOWN);
        SendLegacy(icon, PInvoke.WM_RBUTTONUP);
    }

    /// <summary>Middle click.</summary>
    public static void MiddleClick(TrayIcon icon)
    {
        if (icon.Element is { } element)
        {
            SynthesizeClick(element, ClickKind.Middle);
            return;
        }

        SendLegacy(icon, PInvoke.WM_MBUTTONDOWN);
        SendLegacy(icon, PInvoke.WM_MBUTTONUP);
    }

    private enum ClickKind
    {
        Left,
        Right,
        Middle,
    }

    private static unsafe void SynthesizeClick(AutomationElement element, ClickKind kind)
    {
        if (!UiaTrayReader.TryGetClickablePoint(element, out int x, out int y))
        {
            return;
        }

        _ = PInvoke.SetCursorPos(x, y);

        (MOUSE_EVENT_FLAGS down, MOUSE_EVENT_FLAGS up) = kind switch
        {
            ClickKind.Right => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP),
            ClickKind.Middle => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
        };

        Span<INPUT> inputs =
        [
            MouseInput(down),
            MouseInput(up),
        ];

        fixed (INPUT* p = inputs)
        {
            _ = PInvoke.SendInput((uint)inputs.Length, p, sizeof(INPUT));
        }
    }

    private static INPUT MouseInput(MOUSE_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT { dwFlags = flags },
        },
    };

    private static unsafe void SendLegacy(TrayIcon icon, uint mouseMessage)
    {
        if (icon.OwnerHwnd == 0 || icon.CallbackMessage == 0)
        {
            return;
        }

        var owner = (HWND)icon.OwnerHwnd;
        if (!PInvoke.IsWindow(owner))
        {
            return;
        }

        uint pid = icon.ProcessId;
        if (pid == 0)
        {
            _ = PInvoke.GetWindowThreadProcessId(owner, &pid);
        }

        if (pid != 0)
        {
            _ = PInvoke.AllowSetForegroundWindow(pid);
        }

        WPARAM wParam;
        LPARAM lParam;
        if (icon.Version >= 4)
        {
            // NOTIFYICON_VERSION_4 packing: wParam = cursor point, lParam = LOWORD is
            // the mouse message, HIWORD is the icon id (see .llm/notes.md).
            _ = PInvoke.GetCursorPos(out System.Drawing.Point pt);
            wParam = (nuint)MakeLong((short)pt.X, (short)pt.Y);
            lParam = (nint)MakeLong((short)mouseMessage, (short)icon.Id);
        }
        else
        {
            // Classic packing: wParam = icon id, lParam = mouse message.
            wParam = (nuint)icon.Id;
            lParam = (nint)mouseMessage;
        }

        _ = PInvoke.SendNotifyMessage(owner, icon.CallbackMessage, wParam, lParam);

        // Version 4 clients also expect a follow-up notification for select / context menu.
        if (icon.Version >= 4)
        {
            uint follow = mouseMessage switch
            {
                PInvoke.WM_LBUTTONUP => NIN_SELECT,
                PInvoke.WM_RBUTTONUP => WM_CONTEXTMENU,
                _ => 0,
            };

            if (follow != 0)
            {
                _ = PInvoke.GetCursorPos(out System.Drawing.Point pt2);
                WPARAM w = (nuint)MakeLong((short)pt2.X, (short)pt2.Y);
                LPARAM l = (nint)MakeLong((short)follow, (short)icon.Id);
                _ = PInvoke.SendNotifyMessage(owner, icon.CallbackMessage, w, l);
            }
        }
    }

    private const uint NIN_SELECT = 0x0400;      // WM_USER + 0
    private const uint WM_CONTEXTMENU = 0x007B;

    private static int MakeLong(short low, short high) =>
        (ushort)low | (high << 16);
}
