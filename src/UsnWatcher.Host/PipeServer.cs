using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;

namespace UsnWatcher.Host
{
    public sealed class PipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly List<StreamWriter> _clients = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();

        public PipeServer(char volumeLetter)
        {
            // Use a simple pipe name (NamedPipeServerStream expects the name only).
            _pipeName = $"usn-watcher-{char.ToUpperInvariant(volumeLetter)}";
            // Start acceptor loop
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public string PipeName => _pipeName;

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough
                    );

                    // Wait for a client to connect without blocking other acceptors
                    try
                    {
                        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                        try { Console.WriteLine($"[PIPE] Connection accepted on {_pipeName}"); } catch { }
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        try { server.Dispose(); } catch { }
                        continue;
                    }

                    // When a client connects, start a background task to manage it and immediately loop to accept the next
                    _ = Task.Run(() => HandleClientAsync(server, ct));
                }
            }
            catch { /* swallow */ }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            StreamWriter? writer = null;
            try
            {
                writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };
                lock (_lock) { _clients.Add(writer); try { Console.WriteLine($"[PIPE] Client added (clients={_clients.Count})"); } catch { } }

                // Keep the stream alive until disconnected
                var buffer = new byte[1];
                while (!ct.IsCancellationRequested && server.IsConnected)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (writer != null)
                {
                    lock (_lock) { _clients.Remove(writer); }
                    try { writer.Dispose(); } catch { }
                }
                try { server.Dispose(); } catch { }
            }
        }

        public void Broadcast(string ndjsonLine)
        {
            if (string.IsNullOrEmpty(ndjsonLine)) return;
            // Snapshot the client list so we don't hold the lock while writing.
            List<StreamWriter> snapshot;
            lock (_lock) { snapshot = new List<StreamWriter>(_clients); }

            foreach (var w in snapshot)
            {
                var failed = false;
                try
                {
                    // Use the async write but wait with a short timeout so a stalled/slow client
                    // cannot block the main thread indefinitely.
                    var writeTask = w.WriteLineAsync(ndjsonLine);
                    // Wait a short time for the write to complete.
                    if (!writeTask.Wait(500)) // 500ms
                    {
                        failed = true; // timed out
                    }
                    else
                    {
                        // Observe any exceptions from the task
                        writeTask.GetAwaiter().GetResult();
                    }
                }
                catch
                {
                    failed = true;
                }

                if (failed)
                {
                    lock (_lock)
                    {
                        if (_clients.Remove(w))
                        {
                            try { w.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            lock (_lock)
            {
                foreach (var w in _clients) try { w.Dispose(); } catch { }
                _clients.Clear();
            }
        }
    }
}
