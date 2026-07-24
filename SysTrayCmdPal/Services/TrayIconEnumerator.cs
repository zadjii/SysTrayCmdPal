// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using Windows.Win32;
using Windows.Win32.Foundation;
using SysTrayCmdPal.Interop;
using SysTrayCmdPal.Pages;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Facade that returns the current tray icons. The preferred source is the injected
/// hook (real icons + push updates + robust invocation, on Win10 and Win11). When
/// injection is unavailable it falls back to the classic toolbar reader (Win10) and
/// finally UI Automation (Win11).
/// </summary>
internal static class TrayIconEnumerator
{
    public static IReadOnlyList<TrayIcon> GetIcons(TrayScope scope)
    {
        TrayHook.EnsureStarted();

        IReadOnlyList<HookRecord> hookIcons = TrayHook.Store.Snapshot();
        if (hookIcons.Count > 0)
        {
            return FromHook(hookIcons, scope);
        }

        // Fallbacks: classic toolbar (Win10) then UI Automation (Win11).
        IReadOnlyList<TrayIcon> legacy = LegacyTrayReader.GetIcons();
        if (legacy.Count > 0)
        {
            return FilterVisible(legacy, scope);
        }

        var result = new List<TrayIcon>(UiaTrayReader.GetVisibleIcons());
        if (scope == TrayScope.All)
        {
            result.AddRange(UiaTrayReader.GetOverflowIconsIfOpen());
        }

        return result;
    }

    private static IReadOnlyList<TrayIcon> FromHook(IReadOnlyList<HookRecord> records, TrayScope scope)
    {
        // Icons owned by explorer itself are the shell's own system indicators
        // (volume, network, battery, "safely remove", bluetooth) — on Win11 these
        // live in the combined "omni button" and can't be meaningfully invoked as
        // individual items, so exclude them.
        uint explorerPid = HookInjector.GetExplorerPid();

        // The hook sees every icon but not which are promoted to the taskbar; ask UI
        // Automation which names are currently visible so the two bands can differ.
        HashSet<string>? visibleNames = scope == TrayScope.VisibleOnly ? GetVisibleNames() : null;

        var icons = new List<TrayIcon>(records.Count);
        foreach (HookRecord r in records)
        {
            if (IsShellOwned(r.HWnd, explorerPid))
            {
                continue;
            }

            bool isVisible = visibleNames is null || visibleNames.Contains(Normalize(r.Name));
            if (scope == TrayScope.VisibleOnly && !isVisible)
            {
                continue;
            }

            icons.Add(new TrayIcon
            {
                OwnerHwnd = (nint)r.HWnd,
                Id = r.Id,
                CallbackMessage = r.CallbackMessage,
                Version = r.Version,
                ItemGuid = r.Guid,
                IconPngPath = r.IconPngPath,
                IconVersion = r.IconVersion,
                Tooltip = r.Name,
                IsVisible = isVisible,
            });
        }

        return icons;
    }

    private static unsafe bool IsShellOwned(ulong hwnd, uint explorerPid)
    {
        if (hwnd == 0 || explorerPid == 0)
        {
            return false;
        }

        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId((HWND)(nint)hwnd, &pid);
        return pid == explorerPid;
    }

    private static readonly object VisibleCacheGate = new();
    private static HashSet<string> s_visibleNames = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_visibleAsOf = DateTime.MinValue;

    private static HashSet<string> GetVisibleNames()
    {
        // A UIA descendants walk is expensive; cache it briefly so frequent rebuilds
        // (e.g. driven by a live-updating icon) don't each re-walk the tree.
        lock (VisibleCacheGate)
        {
            if (DateTime.UtcNow - s_visibleAsOf < TimeSpan.FromSeconds(2))
            {
                return s_visibleNames;
            }
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (TrayIcon icon in UiaTrayReader.GetVisibleIcons())
            {
                set.Add(Normalize(icon.Tooltip));
            }
        }
        catch (Exception)
        {
        }

        lock (VisibleCacheGate)
        {
            s_visibleNames = set;
            s_visibleAsOf = DateTime.UtcNow;
        }

        return set;
    }

    private static string Normalize(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private static IReadOnlyList<TrayIcon> FilterVisible(IReadOnlyList<TrayIcon> icons, TrayScope scope)
    {
        if (scope != TrayScope.VisibleOnly)
        {
            return icons;
        }

        var visible = new List<TrayIcon>();
        foreach (TrayIcon icon in icons)
        {
            if (icon.IsVisible)
            {
                visible.Add(icon);
            }
        }

        return visible;
    }
}
