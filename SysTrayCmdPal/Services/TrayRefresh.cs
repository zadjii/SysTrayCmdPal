// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.Win32;
using Windows.Win32.Foundation;
using SysTrayCmdPal.Interop;

namespace SysTrayCmdPal.Services;

/// <summary>Helpers for forcing the shell to rebuild its notification area.</summary>
internal static class TrayRefresh
{
    /// <summary>
    /// Broadcasts the registered <c>TaskbarCreated</c> message so that every app
    /// re-adds its tray icon. Useful when enumeration comes back empty.
    /// </summary>
    public static void RequestTaskbarRebuild()
    {
        uint msg = PInvoke.RegisterWindowMessage("TaskbarCreated");
        if (msg == 0)
        {
            return;
        }

        _ = PInvoke.SendNotifyMessage((HWND)NativeTray.HWND_BROADCAST, msg, default, default);
    }
}
