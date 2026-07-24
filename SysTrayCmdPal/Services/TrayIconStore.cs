// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Thread-safe collection of the tray icons reported by the injected hook. Messages
/// arrive over the named pipe (see <see cref="HookPipeServer"/>) as add/modify/delete
/// events; this store applies them and exposes a snapshot to the UI.
/// </summary>
internal sealed class TrayIconStore
{
    private const uint WireMagic = 0x53545243; // 'STRC'
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;

    private static readonly string IconDir =
        Path.Combine(Path.GetTempPath(), "SysTrayCmdPal", "hookicons");

    private readonly ConcurrentDictionary<string, HookRecord> _icons = new();

    /// <summary>Raised whenever the set of icons changes.</summary>
    public event Action? Changed;

    /// <summary>Current icons, in a stable order.</summary>
    public IReadOnlyList<HookRecord> Snapshot()
    {
        // Collapse all icons that share an owner window into one row. Some apps
        // (e.g. Task Manager) register several icons on the same window with
        // different ids/tooltips as they live-update ("CPU 41%" vs "Task Manager");
        // keep the highest id, which is the stable app-identity registration.
        var latest = new Dictionary<ulong, HookRecord>();
        foreach (HookRecord r in _icons.Values)
        {
            if (!latest.TryGetValue(r.HWnd, out HookRecord? kept) || r.Id > kept.Id)
            {
                latest[r.HWnd] = r;
            }
        }

        var list = new List<HookRecord>(latest.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Drop everything (e.g. after explorer restarts).</summary>
    public void Clear()
    {
        if (!_icons.IsEmpty)
        {
            _icons.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>Apply one wire message received from the hook.</summary>
    public void Apply(byte[] msg, int length)
    {
        if (length < 68)
        {
            return;
        }

        if (BitConverter.ToUInt32(msg, 0) != WireMagic)
        {
            return;
        }

        uint nim = BitConverter.ToUInt32(msg, 4);
        ulong hwnd = BitConverter.ToUInt64(msg, 8);
        uint uid = BitConverter.ToUInt32(msg, 16);
        uint flags = BitConverter.ToUInt32(msg, 20);
        uint callback = BitConverter.ToUInt32(msg, 24);
        uint version = BitConverter.ToUInt32(msg, 28);
        uint guidPresent = BitConverter.ToUInt32(msg, 32);
        Guid? guid = guidPresent != 0 ? new Guid(msg.AsSpan(36, 16)) : null;
        int tipBytes = (int)BitConverter.ToUInt32(msg, 52);
        int w = (int)BitConverter.ToUInt32(msg, 56);
        int h = (int)BitConverter.ToUInt32(msg, 60);
        int iconBytes = (int)BitConverter.ToUInt32(msg, 64);

        int offset = 68;
        string tip = tipBytes > 0 && offset + tipBytes <= length
            ? Encoding.Unicode.GetString(msg, offset, tipBytes).TrimEnd('\0')
            : string.Empty;
        offset += tipBytes;

        string key = guid is { } g ? "guid-" + g.ToString("N") : $"{hwnd:X}-{uid}";

        if (nim == NimDelete)
        {
            if (_icons.TryRemove(key, out _))
            {
                Changed?.Invoke();
            }

            return;
        }

        if (nim != NimAdd && nim != NimModify && nim != NimSetVersion)
        {
            return;
        }

        HookRecord record = _icons.TryGetValue(key, out HookRecord? existing)
            ? existing
            : new HookRecord { Key = key, HWnd = hwnd, Id = uid, Guid = guid };

        bool isNew = existing is null;
        bool changed = isNew;

        record.HWnd = hwnd;
        record.Id = uid;
        record.Guid = guid;

        if ((flags & NifMessage) != 0 || callback != 0)
        {
            record.CallbackMessage = callback;
        }

        if (nim == NimSetVersion || version != 0)
        {
            record.Version = version;
        }

        if ((flags & NifTip) != 0 || (tip.Length > 0 && string.IsNullOrEmpty(record.Name)))
        {
            string newName = FirstLine(tip);
            if (!string.Equals(record.Name, newName, StringComparison.Ordinal))
            {
                record.Name = newName;
                changed = true;
            }
        }

        if ((flags & NifIcon) != 0 && iconBytes > 0 && w > 0 && h > 0 && offset + iconBytes <= length)
        {
            int hash = HashPixels(msg, offset, iconBytes);

            // Only re-encode + bump the version when the pixels actually changed, so
            // static icons keep a stable PNG the UI can reuse without flicker.
            if (hash != record.IconHash || record.IconPngPath is null)
            {
                var rgba = new byte[iconBytes];
                Array.Copy(msg, offset, rgba, 0, iconBytes);
                string? path = WritePng(key, rgba, w, h, record.IconVersion + 1);
                if (path is not null)
                {
                    DeleteQuietly(record.IconPngPath);
                    record.IconPngPath = path;
                    record.IconHash = hash;
                    record.IconVersion++;
                    changed = true;
                }
            }
        }

        _icons[key] = record;

        // Only notify when something the UI cares about actually changed. This keeps
        // pure metadata churn (e.g. repeated no-op modifies) from repainting the band.
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static int HashPixels(byte[] buffer, int offset, int count)
    {
        // FNV-1a over the RGBA bytes.
        unchecked
        {
            const int prime = 16777619;
            int hash = (int)2166136261;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                hash = (hash ^ buffer[i]) * prime;
            }

            return hash;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private static string? WritePng(string key, byte[] rgba, int w, int h, int version)
    {
        try
        {
            byte[] png = PngWriter.Encode(rgba, w, h);
            Directory.CreateDirectory(IconDir);
            // Version the filename so a changed icon lands at a new path; this lets the
            // UI cheaply detect "same path == same bitmap" and avoid reloading it.
            string path = Path.Combine(IconDir, Sanitize(key) + "_v" + version + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Sanitize(string key)
    {
        Span<char> chars = stackalloc char[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '_';
        }

        return new string(chars);
    }
}

/// <summary>Mutable record of one tray icon as reported by the hook.</summary>
internal sealed class HookRecord
{
    public string Key { get; init; } = string.Empty;

    public ulong HWnd { get; set; }

    public uint Id { get; set; }

    public uint CallbackMessage { get; set; }

    public uint Version { get; set; }

    public Guid? Guid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? IconPngPath { get; set; }

    /// <summary>Hash of the current icon pixels, to detect real changes.</summary>
    public int IconHash { get; set; }

    /// <summary>Monotonic counter bumped only when the icon pixels change.</summary>
    public int IconVersion { get; set; }
}
