// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysTrayCmdPal.Interop;
using SysTrayCmdPal.Services;

namespace SysTrayCmdPal.Pages;

/// <summary>Which subset of tray icons a page presents.</summary>
internal enum TrayScope
{
    /// <summary>Every icon the shell knows about.</summary>
    All,

    /// <summary>Only icons currently promoted to the taskbar.</summary>
    VisibleOnly,
}

/// <summary>
/// Lists the current notification-area icons. Rendered as a strip of buttons when
/// used as a dock band, or as a normal list in the palette.
///
/// To avoid flicker, <see cref="ListItem"/> and <see cref="IconInfo"/> instances are
/// cached per icon and mutated in place; new instances are only created when an icon
/// appears or its image actually changes. A content signature gates refreshes so
/// unrelated churn (e.g. Task Manager's per-second updates) doesn't repaint the band.
/// </summary>
internal sealed partial class TrayIconsListPage : ListPage, IDisposable
{
    private static readonly IconInfo FallbackIcon = new("\uE712"); // "More" glyph

    private readonly TrayScope _scope;
    private readonly TaskScheduler _scheduler;
    private readonly Timer _refreshTimer;

    private readonly Dictionary<string, TrayItem> _cache = new(StringComparer.Ordinal);
    private readonly object _buildGate = new();

    private IListItem[] _current = [];
    private string _signature = string.Empty;

    public TrayIconsListPage(TrayScope scope)
    {
        _scope = scope;
        _scheduler = TryGetCurrentScheduler();

        Id = scope == TrayScope.All
            ? "com.zadjii.systray.all"
            : "com.zadjii.systray.visible";
        Name = "Open";
        Title = scope == TrayScope.All ? "All tray icons" : "Visible tray icons";
        Icon = new IconInfo("\uE700");
        ShowDetails = false;

        TrayHook.EnsureStarted();
        TrayHook.Store.Changed += OnStoreChanged;

        // Event-driven refresh handles most updates; this slow timer is a safety net
        // for the fallback (non-hook) enumeration paths. Both go through the
        // signature gate, so a no-op tick costs an enumeration but never repaints.
        _refreshTimer = new Timer(_ => RefreshIfChanged(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        TrayHook.Store.Changed -= OnStoreChanged;
        _refreshTimer.Dispose();
    }

    private static TaskScheduler TryGetCurrentScheduler()
    {
        try
        {
            return SynchronizationContext.Current is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
        }
        catch (InvalidOperationException)
        {
            return TaskScheduler.Default;
        }
    }

    public override IListItem[] GetItems()
    {
        Build();
        return _current;
    }

    internal void RequestRefresh() => RefreshIfChanged();

    private void OnStoreChanged() => RefreshIfChanged();

    /// <summary>Rebuild the item set; only raise ItemsChanged when it actually differs.</summary>
    private void RefreshIfChanged()
    {
        if (!Build())
        {
            return;
        }

        _ = Task.Factory.StartNew(
            () => RaiseItemsChanged(),
            CancellationToken.None,
            TaskCreationOptions.None,
            _scheduler);
    }

    /// <summary>
    /// Recompute the current items, reusing cached instances. Returns true when the
    /// visible result changed since the last build.
    /// </summary>
    private bool Build()
    {
        lock (_buildGate)
        {
            IReadOnlyList<TrayIcon> icons = TrayIconEnumerator.GetIcons(_scope);

            var items = new List<IListItem>(icons.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sig = new StringBuilder();

            foreach (TrayIcon icon in icons)
            {
                string key = icon.StableKey;
                if (!seen.Add(key))
                {
                    continue; // guard against any residual duplicates
                }

                if (!_cache.TryGetValue(key, out TrayItem? entry))
                {
                    entry = new TrayItem(icon);
                    _cache[key] = entry;
                }

                entry.Update(icon, ResolveIconPath(icon));
                items.Add(entry.ListItem);

                sig.Append(key).Append('|')
                   .Append(icon.DisplayName).Append('|')
                   .Append(icon.IconVersion).Append('|')
                   .Append(icon.IsVisible ? '1' : '0').Append(';');
            }

            // Evict cache entries for icons that disappeared.
            if (_cache.Count != seen.Count)
            {
                var stale = new List<string>();
                foreach (string k in _cache.Keys)
                {
                    if (!seen.Contains(k))
                    {
                        stale.Add(k);
                    }
                }

                foreach (string k in stale)
                {
                    _cache.Remove(k);
                }
            }

            if (items.Count == 0)
            {
                if (_signature == "empty")
                {
                    return false;
                }

                _signature = "empty";
                _current = [EmptyItem()];
                return true;
            }

            string signature = sig.ToString();
            if (signature == _signature)
            {
                return false;
            }

            _signature = signature;
            _current = items.ToArray();
            return true;
        }
    }

    private ListItem EmptyItem() => new(new RefreshTrayCommand(this))
    {
        Title = "No tray icons found",
        Subtitle = "Select to refresh",
    };

    /// <summary>Resolve the on-disk PNG path for an icon, or null to use the glyph.</summary>
    private static string? ResolveIconPath(TrayIcon icon) =>
        icon.IconPngPath is { Length: > 0 } hookPng ? hookPng :
        icon.IconHandle != 0 ? TrayIconRenderer.TryRenderToFile(icon.IconHandle, icon.StableKey + "-" + icon.IconHandle) :
        icon.Element is not null ? TrayIconCapture.TryCaptureToFile(icon.Element, icon.StableKey) :
        null;

    /// <summary>
    /// A cached list item plus the mutable command targets behind it. Reused across
    /// refreshes; only touched properties change, which the host observes without
    /// rebuilding the row.
    /// </summary>
    private sealed class TrayItem
    {
        private readonly ActivateTrayIconCommand _activate;
        private readonly ContextMenuTrayIconCommand _context;
        private readonly MiddleClickTrayIconCommand _middle;

        private string? _iconPath;

        public TrayItem(TrayIcon icon)
        {
            _activate = new ActivateTrayIconCommand(icon);
            _context = new ContextMenuTrayIconCommand(icon);
            _middle = new MiddleClickTrayIconCommand(icon);

            ListItem = new ListItem(_activate)
            {
                MoreCommands =
                [
                    new CommandContextItem(_context),
                    new CommandContextItem(_middle),
                ],
            };
        }

        public ListItem ListItem { get; }

        public void Update(TrayIcon icon, string? iconPath)
        {
            _activate.Target = icon;
            _context.Target = icon;
            _middle.Target = icon;

            // Unlabeled icons get no title (never a fabricated "Tray icon N").
            string title = icon.DisplayName;
            if (!string.Equals(ListItem.Title, title, StringComparison.Ordinal))
            {
                ListItem.Title = title;
            }

            string subtitle = icon.IsVisible ? string.Empty : "Overflow";
            if (!string.Equals(ListItem.Subtitle, subtitle, StringComparison.Ordinal))
            {
                ListItem.Subtitle = subtitle;
            }

            // Only swap the IconInfo when the underlying image path changed, so static
            // icons keep the same instance and the host doesn't reload/flicker them.
            if (!string.Equals(_iconPath, iconPath, StringComparison.Ordinal) || ListItem.Icon is null)
            {
                _iconPath = iconPath;
                ListItem.Icon = iconPath is not null ? new IconInfo(iconPath) : FallbackIcon;
            }
        }
    }
}

/// <summary>Left-clicks the tray icon (its default action).</summary>
internal sealed partial class ActivateTrayIconCommand : InvokableCommand
{
    private readonly string _id;

    public ActivateTrayIconCommand(TrayIcon icon)
    {
        Target = icon;
        _id = $"com.zadjii.systray.activate.{icon.StableKey}";
    }

    public TrayIcon Target { get; set; }

    public override string Name => "Activate";

    public override string Id => _id;

    public override IconInfo Icon => new("\uE961"); // click

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.Activate(Target);
        return CommandResult.Dismiss();
    }
}

/// <summary>Right-clicks the tray icon to surface the owning app's native context menu.</summary>
internal sealed partial class ContextMenuTrayIconCommand : InvokableCommand
{
    private readonly string _id;

    public ContextMenuTrayIconCommand(TrayIcon icon)
    {
        Target = icon;
        _id = $"com.zadjii.systray.context.{icon.StableKey}";
    }

    public TrayIcon Target { get; set; }

    public override string Name => "Open context menu";

    public override string Id => _id;

    public override IconInfo Icon => new("\uE712");

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.OpenContextMenu(Target);
        return CommandResult.Dismiss();
    }
}

/// <summary>Middle-clicks the tray icon.</summary>
internal sealed partial class MiddleClickTrayIconCommand : InvokableCommand
{
    private readonly string _id;

    public MiddleClickTrayIconCommand(TrayIcon icon)
    {
        Target = icon;
        _id = $"com.zadjii.systray.middle.{icon.StableKey}";
    }

    public TrayIcon Target { get; set; }

    public override string Name => "Middle click";

    public override string Id => _id;

    public override IconInfo Icon => new("\uE962");

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.MiddleClick(Target);
        return CommandResult.Dismiss();
    }
}

/// <summary>Forces the shell to re-broadcast tray icons, then refreshes the list.</summary>
internal sealed partial class RefreshTrayCommand(TrayIconsListPage page) : InvokableCommand
{
    public override string Name => "Refresh";

    public override string Id => "com.zadjii.systray.refresh";

    public override IconInfo Icon => new("\uE72C");

    public override ICommandResult Invoke()
    {
        TrayRefresh.RequestTaskbarRebuild();
        page.RequestRefresh();
        return CommandResult.KeepOpen();
    }
}
