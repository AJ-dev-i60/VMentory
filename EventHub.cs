using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace HyperInventory;

// Manages SSE subscriber channels and broadcasts events to all connected clients.
public class EventHub
{
    private readonly ConcurrentDictionary<string, Channel> _clients = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string Subscribe()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _clients[id] = new Channel();
        return id;
    }

    public void Unsubscribe(string id) => _clients.TryRemove(id, out _);

    public async Task StreamAsync(string clientId, HttpResponse response, CancellationToken ct)
    {
        if (!_clients.TryGetValue(clientId, out var channel)) return;

        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store, no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt.Data, JsonOpts);
                var msg = $"event: {evt.Type}\ndata: {json}\n\n";
                await response.WriteAsync(msg, ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Unsubscribe(clientId);
        }
    }

    public void Broadcast(string type, object data)
    {
        var evt = new SseEvent { Type = type, Data = data };
        foreach (var ch in _clients.Values)
            ch.Writer.TryWrite(evt);
    }

    private class Channel
    {
        private readonly System.Threading.Channels.Channel<SseEvent> _ch =
            System.Threading.Channels.Channel.CreateUnbounded<SseEvent>();
        public System.Threading.Channels.ChannelReader<SseEvent> Reader => _ch.Reader;
        public System.Threading.Channels.ChannelWriter<SseEvent> Writer => _ch.Writer;
    }
}
