// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows.Automation;
using SysTrayCmdPal.Interop;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Reads tray icons via UI Automation. This is the working path on Windows 11,
/// whose notification area is a XAML island with no classic toolbar to read.
/// </summary>
internal static class UiaTrayReader
{
    private const string ShellTrayClass = "Shell_TrayWnd";
    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
    private const string ShowHiddenName = "Show Hidden Icons";

    /// <summary>
    /// Enumerate the tray icons currently promoted onto the taskbar. Non-disruptive.
    /// </summary>
    public static IReadOnlyList<TrayIcon> GetVisibleIcons()
    {
        var results = new List<TrayIcon>();
        AutomationElement? taskbar = FindTaskbar();
        if (taskbar is null)
        {
            return results;
        }

        CollectTrayButtons(taskbar, isVisible: true, results);
        return results;
    }

    /// <summary>
    /// Enumerate the hidden ("overflow") icons, but only if the overflow flyout is
    /// currently open. Reading them does not force the flyout open (that would be
    /// disruptive); use <see cref="TryShowHiddenIcons"/> to open it explicitly.
    /// </summary>
    public static IReadOnlyList<TrayIcon> GetOverflowIconsIfOpen()
    {
        var results = new List<TrayIcon>();
        try
        {
            AutomationElement? overflow = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, OverflowClass));
            if (overflow is not null)
            {
                CollectTrayButtons(overflow, isVisible: false, results);
            }
        }
        catch (Exception)
        {
            // UIA can throw transiently while the flyout is animating; ignore.
        }

        return results;
    }

    /// <summary>Invoke the "Show Hidden Icons" chevron to open the overflow flyout.</summary>
    public static bool TryShowHiddenIcons()
    {
        AutomationElement? taskbar = FindTaskbar();
        if (taskbar is null)
        {
            return false;
        }

        try
        {
            AutomationElement? chevron = taskbar.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, ShowHiddenName));
            if (chevron is not null && TryInvoke(chevron))
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>Left-click / default-activate an element.</summary>
    public static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object invokeObj)
                && invokeObj is InvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>Best-effort clickable point for synthesizing a right/middle click.</summary>
    public static bool TryGetClickablePoint(AutomationElement element, out int x, out int y)
    {
        x = 0;
        y = 0;
        try
        {
            if (element.TryGetClickablePoint(out System.Windows.Point point))
            {
                x = (int)point.X;
                y = (int)point.Y;
                return true;
            }

            System.Windows.Rect rect = element.Current.BoundingRectangle;
            if (!rect.IsEmpty)
            {
                x = (int)(rect.X + (rect.Width / 2));
                y = (int)(rect.Y + (rect.Height / 2));
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static AutomationElement? FindTaskbar()
    {
        try
        {
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, ShellTrayClass));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CollectTrayButtons(AutomationElement root, bool isVisible, List<TrayIcon> results)
    {
        try
        {
            AutomationElementCollection buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            foreach (AutomationElement button in buttons)
            {
                if (!IsTrayIconButton(button, out string name))
                {
                    continue;
                }

                results.Add(new TrayIcon
                {
                    Element = button,
                    Tooltip = name,
                    IsVisible = isVisible,
                });
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool IsTrayIconButton(AutomationElement button, out string name)
    {
        name = string.Empty;
        try
        {
            string className = button.Current.ClassName ?? string.Empty;
            if (!className.StartsWith("SystemTray.", StringComparison.Ordinal))
            {
                return false;
            }

            string label = (button.Current.Name ?? string.Empty).Trim();
            if (label.Equals(ShowHiddenName, StringComparison.OrdinalIgnoreCase))
            {
                return false; // the chevron itself is not an icon
            }

            // Collapse multi-line accessible names (e.g. "Spotify\nPlaying") to the first line.
            int newline = label.IndexOfAny(['\r', '\n']);
            name = newline >= 0 ? label[..newline].Trim() : label;
            return !string.IsNullOrWhiteSpace(name);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
