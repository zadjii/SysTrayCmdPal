// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Process-wide owner of the tray-icon hook: the store of icons and the pipe server
/// that injects the hook and feeds the store. Started lazily on first use.
/// </summary>
internal static class TrayHook
{
    private static readonly object Gate = new();
    private static HookPipeServer? s_server;

    /// <summary>The live set of icons reported by the hook.</summary>
    public static TrayIconStore Store { get; } = new();

    /// <summary>True once the pipe server has been started (regardless of connection state).</summary>
    public static bool Started { get; private set; }

    /// <summary>Start injection + pipe server exactly once.</summary>
    public static void EnsureStarted()
    {
        if (Started)
        {
            return;
        }

        lock (Gate)
        {
            if (Started)
            {
                return;
            }

            string? dll = ResolveDllPath();
            if (dll is not null)
            {
                s_server = new HookPipeServer(Store, dll);
                s_server.Start();
            }

            Started = true;
        }
    }

    private static string? ResolveDllPath()
    {
        // Dev override so the DLL can be exercised without packaging.
        string? overridePath = Environment.GetEnvironmentVariable("SYSTRAYCMDPAL_HOOK_DLL");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        string local = Path.Combine(AppContext.BaseDirectory, "SysTrayHook.dll");
        return File.Exists(local) ? local : null;
    }
}
