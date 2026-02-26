using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        var clients = new ConcurrentDictionary<Guid, WebSocket>();

        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:9876/");
        listener.Start();
        Console.WriteLine("Listening for WebSocket connections on ws://localhost:9876/");

        // Accept incoming WebSocket connections
        var acceptTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("HttpListener error: " + ex);
                    break;
                }

                if (ctx.Request.IsWebSocketRequest)
                {
                    try
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                        var ws = wsCtx.WebSocket;
                        var id = Guid.NewGuid();
                        clients[id] = ws;
                        Console.WriteLine("Client connected: " + id);

                        // Keep receiving to detect client disconnect
                        _ = Task.Run(async () =>
                        {
                            var buffer = new byte[1024];
                            try
                            {
                                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                                {
                                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                                    if (result.MessageType == WebSocketMessageType.Close) break;
                                }
                            }
                            catch { }
                            finally
                            {
                                clients.TryRemove(id, out _);
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                                Console.WriteLine("Client disconnected: " + id);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("WebSocket accept error: " + ex);
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                }
            }
        });

        // Read NDJSON lines from named pipe and broadcast to WebSocket clients
        var pipeTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", "usn-watcher-C", PipeDirection.In, PipeOptions.Asynchronous);
                    Console.WriteLine("Connecting to pipe \\\\.\\pipe\\usn-watcher-C...");
                    pipe.Connect(5000);
                    Console.WriteLine("Connected to pipe.");
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (!cts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break; // pipe closed, reconnect
                        var preview = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                        Console.WriteLine($"Broadcasting ({clients.Count} clients): {preview}");
                        await BroadcastAsync(clients, line, cts.Token);
                    }
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Pipe connection timeout, retrying...");
                    await Task.Delay(1000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Pipe error: " + ex);
                    await Task.Delay(1000, cts.Token);
                }
            }
        });

        await Task.WhenAny(acceptTask, pipeTask);
        cts.Cancel();
        listener.Stop();
    }

    static async Task BroadcastAsync(ConcurrentDictionary<Guid, WebSocket> clients, string message, CancellationToken ct)
    {
        var buffer = Encoding.UTF8.GetBytes(message);
        var seg = new ArraySegment<byte>(buffer);
        foreach (var kvp in clients)
        {
            var ws = kvp.Value;
            if (ws.State != WebSocketState.Open)
            {
                clients.TryRemove(kvp.Key, out _);
                continue;
            }
            try
            {
                await ws.SendAsync(seg, WebSocketMessageType.Text, true, ct);
            }
            catch
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Send failed", CancellationToken.None); } catch { }
                clients.TryRemove(kvp.Key, out _);
            }
        }
    }
}
