// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace SysTrayCmdPal.Services;

/// <summary>
/// Owns the named pipe that the injected hook connects to, drives injection and
/// its lifecycle (including explorer restarts), and forwards received messages to
/// a <see cref="TrayIconStore"/>. Runs on a dedicated background thread.
/// </summary>
internal sealed class HookPipeServer : IDisposable
{
    private const string PipeName = "SysTrayCmdPal.systray";
    private const string DllName = "SysTrayHook.dll";
    private const int MaxMessage = 1 * 1024 * 1024;

    private readonly TrayIconStore _store;
    private readonly string _dllPath;
    private readonly Thread _thread;

    private volatile bool _running = true;
    private NamedPipeServerStream? _current;

    public HookPipeServer(TrayIconStore store, string dllPath)
    {
        _store = store;
        _dllPath = dllPath;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "SysTrayHookPipe",
        };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _running = false;
        try
        {
            _current?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                RunOnce();
            }
            catch (Exception)
            {
                // Keep the loop alive across transient failures.
            }

            if (_running)
            {
                Thread.Sleep(2000);
            }
        }
    }

    private void RunOnce()
    {
        if (!File.Exists(_dllPath))
        {
            return;
        }

        using var server = new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.None,
            MaxMessage,
            0);
        _current = server;

        uint pid = HookInjector.GetExplorerPid();
        nint hook = 0;
        bool alreadyInjected = pid != 0 && HookInjector.IsInjected(pid, DllName);
        if (pid != 0 && !alreadyInjected)
        {
            hook = HookInjector.Inject(_dllPath)?.Hook ?? 0;
        }
        else if (alreadyInjected)
        {
            // The DLL is resident from a previous session; it only reconnects when it
            // next sees a tray message, so prod apps into re-adding their icons.
            HookInjector.BroadcastTaskbarCreated();
        }

        try
        {
            server.WaitForConnection();
        }
        catch (Exception)
        {
            HookInjector.Unhook(hook);
            return;
        }

        // The hook is only an injection vehicle; once the DLL is connected we no
        // longer need it, and the DLL's Shell_TrayWnd subclass does the real work.
        HookInjector.Unhook(hook);

        // Existing apps only (re)send their icons on TaskbarCreated.
        HookInjector.BroadcastTaskbarCreated();

        ReadMessages(server);
    }

    private void ReadMessages(NamedPipeServerStream server)
    {
        var chunk = new byte[64 * 1024];
        using var assembled = new MemoryStream();

        while (_running && server.IsConnected)
        {
            assembled.SetLength(0);
            int read;
            try
            {
                do
                {
                    read = server.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    assembled.Write(chunk, 0, read);
                }
                while (!server.IsMessageComplete && assembled.Length < MaxMessage);
            }
            catch (Exception)
            {
                break;
            }

            if (read <= 0 && assembled.Length == 0)
            {
                break;
            }

            if (assembled.Length >= 68)
            {
                _store.Apply(assembled.GetBuffer(), (int)assembled.Length);
            }
        }
    }
}
