using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class LighterWebSocketClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly ILogger<LighterWebSocketClient> _logger;
    private readonly List<string> _subscribedChannels = new();
    private int _reconnectAttempts;

    internal const string MainnetUrl = "wss://mainnet.zklighter.elliot.ai/stream";
    internal static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReconnectBaseDelay = TimeSpan.FromSeconds(2);
    private const int MaxReconnectDelaySeconds = 60;
    private const int MaxReconnectAttempts = 10;
    private const int ReceiveBufferSize = 8192;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public event Action<string, JsonElement>? OnMessage;
    public event Action<string>? OnDisconnected;

    public LighterWebSocketClient(ILogger<LighterWebSocketClient> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.Zero; // We handle keepalive manually

        await _ws.ConnectAsync(new Uri(MainnetUrl), ct);
        _reconnectAttempts = 0;

        _logger.LogInformation("Lighter WebSocket connected to {Url}", MainnetUrl);

        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(token), token);
        _ = Task.Run(() => KeepaliveLoopAsync(token), token);
    }

    public async Task SubscribeAsync(string channel, string? authToken = null, CancellationToken ct = default)
    {
        var msg = authToken is not null
            ? JsonSerializer.Serialize(new { type = "subscribe", channel, auth = authToken })
            : JsonSerializer.Serialize(new { type = "subscribe", channel });

        await SendTextAsync(msg, ct);

        lock (_subscribedChannels)
        {
            if (!_subscribedChannels.Contains(channel))
                _subscribedChannels.Add(channel);
        }

        _logger.LogDebug("Lighter WS subscribed to {Channel}", channel);
    }

    public async Task UnsubscribeAsync(string channel, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "unsubscribe", channel });
        await SendTextAsync(msg, ct);

        lock (_subscribedChannels)
        {
            _subscribedChannels.Remove(channel);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("Lighter WS received close frame: {Status} {Description}",
                            result.CloseStatus, result.CloseStatusDescription);
                        OnDisconnected?.Invoke($"Server close: {result.CloseStatusDescription}");
                        await ReconnectAsync(ct);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Position = 0;
                    ProcessMessage(ms);
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Lighter WS receive error");
            OnDisconnected?.Invoke($"WebSocket error: {ex.Message}");
            if (!ct.IsCancellationRequested)
                await ReconnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Lighter WS receive loop");
            OnDisconnected?.Invoke($"Unexpected error: {ex.Message}");
            if (!ct.IsCancellationRequested)
                await ReconnectAsync(ct);
        }
    }

    private void ProcessMessage(MemoryStream ms)
    {
        try
        {
            using var doc = JsonDocument.Parse(ms);
            var root = doc.RootElement;

            // Handle server ping
            if (root.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "ping")
            {
                _ = SendTextAsync("{\"type\":\"pong\"}", CancellationToken.None);
                return;
            }

            // Dispatch channel messages
            if (root.TryGetProperty("channel", out var channelProp))
            {
                var channel = channelProp.GetString() ?? "";
                OnMessage?.Invoke(channel, root.Clone());
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse Lighter WS message");
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, ct);
                if (_ws?.State == WebSocketState.Open)
                {
                    await SendTextAsync("{\"type\":\"pong\"}", ct);
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lighter WS keepalive error");
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            var delaySec = Math.Min(
                (int)(ReconnectBaseDelay.TotalSeconds * Math.Pow(2, _reconnectAttempts - 1)),
                MaxReconnectDelaySeconds);

            _logger.LogInformation("Lighter WS reconnecting in {Delay}s (attempt {Attempt}/{Max})",
                delaySec, _reconnectAttempts, MaxReconnectAttempts);

            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.Zero;

                await _ws.ConnectAsync(new Uri(MainnetUrl), ct);
                _reconnectAttempts = 0;
                _logger.LogInformation("Lighter WS reconnected");

                // Re-subscribe all channels
                List<string> channels;
                lock (_subscribedChannels)
                {
                    channels = new List<string>(_subscribedChannels);
                }

                foreach (var channel in channels)
                {
                    var msg = JsonSerializer.Serialize(new { type = "subscribe", channel });
                    await SendTextAsync(msg, ct);
                }

                // Restart loops
                var token = _cts?.Token ?? ct;
                _ = Task.Run(() => ReceiveLoopAsync(token), token);
                _ = Task.Run(() => KeepaliveLoopAsync(token), token);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lighter WS reconnect attempt {Attempt} failed", _reconnectAttempts);
            }
        }

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            _logger.LogCritical("Lighter WS gave up reconnecting after {Max} attempts", MaxReconnectAttempts);
        }
    }

    private async Task SendTextAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS already disposed during reconnect */ }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
            }
            catch { /* Best-effort close */ }
        }

        _ws?.Dispose();

        try { _cts?.Dispose(); }
        catch (ObjectDisposedException) { /* Already disposed */ }
    }
}
