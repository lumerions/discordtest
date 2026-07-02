using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebSockets;
using System.Net.WebSockets;
using System.Text;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Text.Json;
using Internal.Redis;
using System.Net.Sockets;
using StackExchange.Redis;
using Internal.MainController;

namespace Internal.WebSocketController;

public class WebSocketSessionManager {
    public ConcurrentDictionary<string, WebSocket> Users = new();
}

public class SocketMessage
{
    public string Type {get; set;}
    public int UserId {get; set;}
    public string? Message {get; set;}
    public int DiscordChannelId {get; set;}
}

[ApiController]
[Route("/ws/{controller}")]
public class WebSocketController : ControllerBase
{
    private readonly WebSocketSessionManager Manager;
    private readonly IDatabase RedisDatabase;

    public WebSocketController(WebSocketSessionManager manager, RedisHandler redis_)
    {
        Manager = manager;
        RedisDatabase = redis_.GetRedisDatabase();
    }

    [HttpGet]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return;
        }

        var UserId = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var Username = HttpContext.User.FindFirst(ClaimTypes.Name)?.Value;

        if (string.IsNullOrWhiteSpace(UserId)) return;
        if (string.IsNullOrWhiteSpace(Username)) return;

        WebSocket websocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        try {

        Manager.Users.TryAdd(UserId, websocket);
        var buffer = new byte[1024  * 4];

        while (websocket.State == WebSocketState.Open)
        {
            var result = await websocket.ReceiveAsync(new ArraySegment<byte> (buffer), CancellationToken.None);
            var exit = false;

            switch (result.MessageType)
            {
                case WebSocketMessageType.Text:
                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var SocketJSON = JsonSerializer.Deserialize<SocketMessage>(text);

                    if (text.StartsWith("{")) {
                        var DiscordChannelId = SocketJSON?.DiscordChannelId;
                        var SocketJSONType = SocketJSON?.Type;

                        if (DiscordChannelId == null) break;
                        if (SocketJSONType == null) break;

                        var RedisKey = $"channels:{DiscordChannelId.ToString()}";
                        var fullKey = DiscordChannelId.ToString() + ";" + Username.ToString() + ";" + UserId.ToString();
                        switch (SocketJSONType)
                        {
                            case "Typing":
                                await RedisDatabase.SetAddAsync(RedisKey, fullKey);
                                break;
                            case "NoTyping":
                                await RedisDatabase.SetRemoveAsync(RedisKey, fullKey);
                                break;
                        }
                        // TODO add actual auth lol
                        // TODO send message to all users viewing this channel and send a message telling them to poll for the current users typing
                        var SocketType = Encoding.UTF8.GetBytes(SocketJSONType);
                        var SocketTypeBuffer = new ArraySegment<byte> (SocketType);
                        await websocket.SendAsync(SocketTypeBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    break;
                case WebSocketMessageType.Close:
                    await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    exit = true;
                    break;
            }

            if (exit)
            {
                break;
            }
        }

        } finally
        {
            Manager.Users.TryRemove(UserId.ToString(), out _);
            websocket.Dispose();
        }
    }
}