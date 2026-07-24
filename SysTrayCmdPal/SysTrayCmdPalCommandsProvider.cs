// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysTrayCmdPal.Pages;

namespace SysTrayCmdPal;

public partial class SysTrayCmdPalCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _bands;

    public SysTrayCmdPalCommandsProvider()
    {
        DisplayName = "System Tray for Command Palette";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _commands =
        [
            new CommandItem(new TrayIconsListPage(TrayScope.All)) { Title = DisplayName },
        ];

        _bands =
        [
            new CommandItem(new TrayIconsListPage(TrayScope.All))
            {
                Title = "System Tray (all icons)",
            },
            new CommandItem(new TrayIconsListPage(TrayScope.VisibleOnly))
            {
                Title = "System Tray (visible icons)",
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[]? GetDockBands()
    {
        return _bands;
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        foreach (ICommandItem item in _bands)
        {
            if (item.Command is { } cmd && cmd.Id == id)
            {
                return item;
            }
        }

        foreach (ICommandItem item in _commands)
        {
            if (item.Command is { } cmd && cmd.Id == id)
            {
                return item;
            }
        }

        return null;
    }
}
