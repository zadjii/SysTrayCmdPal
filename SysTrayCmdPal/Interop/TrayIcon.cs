// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows.Automation;

namespace SysTrayCmdPal.Interop;

/// <summary>
/// Immutable snapshot of a single Windows notification-area ("system tray") icon.
/// Depending on the OS it is sourced either from Explorer's classic toolbar
/// (Win10, carries real Win32 handles) or from UI Automation (Win11, carries an
/// <see cref="AutomationElement"/>).
/// </summary>
internal sealed class TrayIcon
{
    /// <summary>Window that owns the icon and receives its callback messages (legacy path).</summary>
    public nint OwnerHwnd { get; init; }

    /// <summary>Application-defined identifier for this icon within its owner window (legacy path).</summary>
    public uint Id { get; init; }

    /// <summary>The message id the shell posts to <see cref="OwnerHwnd"/> for mouse events (legacy path).</summary>
    public uint CallbackMessage { get; init; }

    /// <summary>Icon handle read from the tray data, if available (legacy path).</summary>
    public nint IconHandle { get; init; }

    /// <summary>Process id that owns <see cref="OwnerHwnd"/> (legacy path).</summary>
    public uint ProcessId { get; init; }

    /// <summary>The UI Automation element backing this icon (Win11 path), if any.</summary>
    public AutomationElement? Element { get; init; }

    /// <summary>Notify-icon version (NOTIFYICON_VERSION*), from the injection hook.</summary>
    public uint Version { get; init; }

    /// <summary>Item GUID, when the app identifies its icon by GUID (hook path).</summary>
    public Guid? ItemGuid { get; init; }

    /// <summary>Path to a rendered PNG of the real icon (hook path), if available.</summary>
    public string? IconPngPath { get; init; }

    /// <summary>Version of the icon pixels; changes only when the image actually changes.</summary>
    public int IconVersion { get; init; }

    /// <summary>Tooltip / accessible name.</summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>True when the icon is promoted to the taskbar (not in the overflow flyout).</summary>
    public bool IsVisible { get; init; }

    /// <summary>Owning process executable name (best-effort), used as a fallback label.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Stable key that survives re-enumeration, used for command ids and diffing.</summary>
    public string StableKey =>
        ItemGuid is { } g ? "guid-" + g.ToString("N") :
        Element is not null
            ? "uia-" + Math.Abs(DisplayName.GetHashCode(StringComparison.Ordinal)).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
            : OwnerHwnd != 0
                ? $"hwnd-{OwnerHwnd:X}"
                : $"{OwnerHwnd:X}-{Id}";

    /// <summary>True when the icon has a real tooltip/label.</summary>
    public bool HasLabel => !string.IsNullOrWhiteSpace(Tooltip);

    /// <summary>
    /// The label to show, or an empty string when the icon has no tooltip. We do not
    /// fabricate a name (e.g. "Tray icon 3") — an unlabeled icon simply shows no title.
    /// </summary>
    public string DisplayName => HasLabel ? Tooltip : string.Empty;
}
