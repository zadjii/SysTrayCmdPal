// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
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
    /// <summary>Every icon, including those tucked into the overflow flyout.</summary>
    All,

    /// <summary>Only icons currently promoted to the taskbar.</summary>
    VisibleOnly,
}

/// <summary>
/// Lists the current notification-area icons. Rendered as a strip of buttons when
/// used as a dock band, or as a normal list in the palette.
/// </summary>
internal sealed partial class TrayIconsListPage : ListPage, IDisposable
{
    private static readonly IconInfo FallbackIcon = new("\uE712"); // "More" glyph

    private readonly TrayScope _scope;
    private readonly TaskScheduler _scheduler;
    private readonly Timer _refreshTimer;

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

        _refreshTimer = new Timer(_ => RequestRefresh(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Dispose() => _refreshTimer.Dispose();

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
        IReadOnlyList<TrayIcon> icons = TrayIconEnumerator.GetIcons(_scope);
        var items = new List<IListItem>(icons.Count);

        foreach (TrayIcon icon in icons)
        {
            items.Add(ToListItem(icon));
        }

        if (_scope == TrayScope.All)
        {
            items.Add(new ListItem(new ShowHiddenIconsCommand(this))
            {
                Title = "Show hidden icons",
                Subtitle = "Open the overflow flyout to reveal hidden tray icons",
                Icon = new IconInfo("\uE712"),
            });
        }

        if (items.Count == 0)
        {
            items.Add(new ListItem(new RefreshTrayCommand(this))
            {
                Title = "No tray icons found",
                Subtitle = "Select to refresh",
            });
        }

        return items.ToArray();
    }

    internal void RequestRefresh()
    {
        _ = Task.Factory.StartNew(
            () => RaiseItemsChanged(),
            CancellationToken.None,
            TaskCreationOptions.None,
            _scheduler);
    }

    private static ListItem ToListItem(TrayIcon icon)
    {
        string? iconPath =
            icon.IconPngPath is { Length: > 0 } hookPng ? hookPng :
            icon.IconHandle != 0 ? TrayIconRenderer.TryRenderToFile(icon.IconHandle, icon.StableKey + "-" + icon.IconHandle) :
            icon.Element is not null ? TrayIconCapture.TryCaptureToFile(icon.Element, icon.StableKey) :
            null;
        IconInfo iconInfo = iconPath is not null ? new IconInfo(iconPath) : FallbackIcon;

        var item = new ListItem(new ActivateTrayIconCommand(icon))
        {
            Title = icon.DisplayName,
            Subtitle = icon.IsVisible ? string.Empty : "Overflow",
            Icon = iconInfo,
            MoreCommands =
            [
                new CommandContextItem(new ContextMenuTrayIconCommand(icon)),
                new CommandContextItem(new MiddleClickTrayIconCommand(icon)),
            ],
        };

        return item;
    }
}

/// <summary>Left-clicks the tray icon (its default action).</summary>
internal sealed partial class ActivateTrayIconCommand(TrayIcon icon) : InvokableCommand
{
    public override string Name => "Activate";
    public override string Id => $"com.zadjii.systray.activate.{icon.StableKey}";
    public override IconInfo Icon => new("\uE961"); // click

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.Activate(icon);
        return CommandResult.Dismiss();
    }
}

/// <summary>Right-clicks the tray icon to surface the owning app's native context menu.</summary>
internal sealed partial class ContextMenuTrayIconCommand(TrayIcon icon) : InvokableCommand
{
    public override string Name => "Open context menu";
    public override string Id => $"com.zadjii.systray.context.{icon.StableKey}";
    public override IconInfo Icon => new("\uE712");

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.OpenContextMenu(icon);
        return CommandResult.Dismiss();
    }
}

/// <summary>Middle-clicks the tray icon.</summary>
internal sealed partial class MiddleClickTrayIconCommand(TrayIcon icon) : InvokableCommand
{
    public override string Name => "Middle click";
    public override string Id => $"com.zadjii.systray.middle.{icon.StableKey}";
    public override IconInfo Icon => new("\uE962");

    public override ICommandResult Invoke()
    {
        TrayIconInvoker.MiddleClick(icon);
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

/// <summary>Opens the Windows "hidden icons" overflow flyout, then refreshes the list.</summary>
internal sealed partial class ShowHiddenIconsCommand(TrayIconsListPage page) : InvokableCommand
{
    public override string Name => "Show hidden icons";
    public override string Id => "com.zadjii.systray.showHidden";
    public override IconInfo Icon => new("\uE712");

    public override ICommandResult Invoke()
    {
        UiaTrayReader.TryShowHiddenIcons();
        page.RequestRefresh();
        return CommandResult.KeepOpen();
    }
}
