using System.Collections.Concurrent;
using StackExchange.Redis;
using System.Net.WebSockets;
using Internal.Redis;
using System.Text;
using System.Text.Json.Nodes;

namespace Internal.Shared;

public class SharedMethods
{


    private readonly WebSocketSessionManager Manager;
    private readonly IDatabase RedisDatabase;
    private readonly WebSocketChannelIdConnections websocketconns_;

    public SharedMethods(WebSocketSessionManager manager, RedisHandler redis_, WebSocketChannelIdConnections  websocketconns)
    {
        Manager = manager;
        RedisDatabase = redis_.GetRedisDatabase();
        websocketconns_ = websocketconns;
    }

    public class WebSocketChannelIdConnections
    {
        public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ChannelUsers = new();
    }

    public class WebSocketSessionManager {
        public ConcurrentDictionary<string, WebSocket> Users = new();
    }

    public async Task SendSocketMessage(int? DiscordChannelId, string? SocketJSONType)
    {
        if (!websocketconns_.ChannelUsers.TryGetValue(DiscordChannelId.ToString(), out var ChannelIds)) return;
        var SocketType = Encoding.UTF8.GetBytes(SocketJSONType);
        var SocketTypeBuffer = new ArraySegment<byte> (SocketType);
        var MessageTasks = new List<Task>();

        async Task SendMessage(string ChannelUserId)
        {
            if (Manager.Users.TryGetValue(ChannelUserId, out var UserSocket)) {
                try {
                    await UserSocket.SendAsync(SocketTypeBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                } catch
                {
                    try
                    {
                        if (UserSocket.State == WebSocketState.CloseReceived || UserSocket.State == WebSocketState.Open)
                        {
                            await UserSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Internal Socket Error.", CancellationToken.None);
                        }
                    }
                    catch {}
                    finally
                    {
                        UserSocket.Dispose();
                        Manager.Users.TryRemove(ChannelUserId, out _);
                    }
                }
            }
        }

        foreach (var ChannelUserId in ChannelIds.Keys)
        {
            MessageTasks.Add(SendMessage(ChannelUserId));
        }

        await Task.WhenAll(MessageTasks);
    }
}
