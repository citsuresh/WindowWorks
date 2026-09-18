using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Minimal hand-rolled Chrome DevTools Protocol (CDP) client: HTTP-based target discovery
    /// plus a WebSocket JSON-RPC connection to a single target, used to send CDP commands and
    /// correlate responses by request id.
    ///
    /// This is sub-phase 1 of docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E — a
    /// proof-of-concept bridge mechanism only. No UI wiring, no element correlation, no property
    /// read/write yet; those are later sub-phases. Chose a hand-rolled client over a NuGet CDP
    /// library per explicit decision recorded in that plan (project deliberately keeps external
    /// dependencies minimal, and the wire protocol needed here is small: one HTTP GET plus a
    /// handful of JSON-RPC-style WebSocket round-trips).
    /// </summary>
    internal sealed class CdpClient : IAsyncDisposable
    {
        private const int MaxMessageBytes = 32 * 1024 * 1024; // 32 MB safety cap on reassembled multi-frame messages

        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
        private int _nextId;
        private Task? _receiveLoop;
        private CancellationTokenSource? _receiveLoopCts;

        // Set once the receive loop exits (error or normal close) so any SendCommandAsync call
        // racing with (or arriving after) the connection dying fails fast instead of hanging
        // forever on a TaskCompletionSource that will never be completed by a dead receive loop.
        private volatile Exception? _faultException;

        /// <summary>
        /// Queries a Chromium DevTools HTTP endpoint (e.g. <c>http://localhost:9222</c>) for its
        /// currently open debuggable targets. Chromium exposes this at <c>/json/list</c> (also
        /// aliased as <c>/json</c>) as plain JSON — no WebSocket needed for discovery itself.
        /// </summary>
        /// <param name="debuggerHttpBaseUrl">
        /// Base URL of the DevTools HTTP endpoint, e.g. <c>http://localhost:9222</c> (no trailing
        /// slash expected; one is added).
        /// </param>
        public static async Task<IReadOnlyList<CdpTarget>> GetTargetsAsync(
            string debuggerHttpBaseUrl,
            CancellationToken cancellationToken = default)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = debuggerHttpBaseUrl.TrimEnd('/') + "/json/list";
            var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var targets = JsonSerializer.Deserialize<List<CdpTarget>>(json);
            return targets ?? new List<CdpTarget>();
        }

        /// <summary>
        /// Opens the WebSocket connection to a specific target's <c>webSocketDebuggerUrl</c> and
        /// starts the background receive loop that dispatches responses to pending requests.
        /// Must be called before <see cref="SendCommandAsync"/>.
        /// </summary>
        public async Task ConnectAsync(string webSocketDebuggerUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(webSocketDebuggerUrl))
            {
                throw new ArgumentException("webSocketDebuggerUrl must not be empty.", nameof(webSocketDebuggerUrl));
            }

            await _socket.ConnectAsync(new Uri(webSocketDebuggerUrl), cancellationToken).ConfigureAwait(false);

            _receiveLoopCts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));
        }

        /// <summary>
        /// Sends a single CDP command (e.g. <c>"Runtime.evaluate"</c>) with the given params
        /// object and awaits its correlated response's <c>result</c> field. Throws if the CDP
        /// response contains an <c>error</c> field.
        /// </summary>
        public async Task<JsonNode?> SendCommandAsync(
            string method,
            object? @params = null,
            CancellationToken cancellationToken = default)
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("CdpClient is not connected. Call ConnectAsync first.");
            }

            if (_faultException is not null)
            {
                throw new InvalidOperationException("CdpClient connection has failed.", _faultException);
            }

            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            // Re-check after registering: closes the narrow window where the receive loop faults
            // and drains _pending concurrently with this method adding a new entry to it.
            if (_faultException is not null && _pending.TryRemove(id, out var raced))
            {
                raced.TrySetException(_faultException);
            }

            var payload = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params is null ? new JsonObject() : JsonSerializer.SerializeToNode(@params)
            };

            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        if (ms.Length + result.Count > MaxMessageBytes)
                        {
                            throw new InvalidOperationException(
                                $"CDP message exceeded the {MaxMessageBytes}-byte safety cap; aborting connection.");
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    DispatchMessage(text);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on disposal/shutdown.
            }
            catch (Exception ex)
            {
                FailAllPending(ex);
            }
            finally
            {
                // Ensure any request that raced the loop's exit (added to _pending after the
                // catch block's snapshot, or after a clean/cancelled exit with no exception) is
                // still failed rather than left to hang forever.
                _faultException ??= new InvalidOperationException("CdpClient receive loop has stopped.");
                FailAllPending(_faultException);
            }
        }

        private void FailAllPending(Exception ex)
        {
            _faultException = ex;
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        private void DispatchMessage(string json)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            if (node is not JsonObject obj)
            {
                return;
            }

            // CDP messages are either command responses (have "id") or unsolicited events (no
            // "id", have "method" instead). Sub-phase 1 only needs responses; events are ignored
            // here and will matter starting with later sub-phases if any are needed.
            if (!obj.TryGetPropertyValue("id", out var idNode) || idNode is null)
            {
                return;
            }

            int id = idNode.GetValue<int>();
            if (!_pending.TryRemove(id, out var tcs))
            {
                return;
            }

            if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is not null)
            {
                tcs.TrySetException(new InvalidOperationException($"CDP command failed: {errorNode.ToJsonString()}"));
                return;
            }

            obj.TryGetPropertyValue("result", out var resultNode);
            tcs.TrySetResult(resultNode);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _receiveLoopCts?.Cancel();
            }
            catch { }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch { }

            try
            {
                if (_receiveLoop is not null)
                {
                    await _receiveLoop.ConfigureAwait(false);
                }
            }
            catch { }

            _socket.Dispose();
            _receiveLoopCts?.Dispose();
        }
    }
}
