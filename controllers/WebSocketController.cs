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

namespace Internal.WebSocketController;

public class WebSocketSessionManager {
    public ConcurrentDictionary<string, WebSocket> Users = new();
}

[ApiController]
[Route("/ws/{controller}")]
public class WebSocketController : ControllerBase
{
    private readonly WebSocketSessionManager Manager;
    public WebSocketController(WebSocketSessionManager manager)
    {
        Manager = manager;
    }

    [HttpGet]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return;
        }

        var UserId = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(UserId)) return;

        WebSocket websocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        Manager.Users[UserId.ToString()] = websocket;
        var buffer = new byte[1024  * 4];

        while (websocket.State == WebSocketState.Open)
        {
            var result = await websocket.ReceiveAsync(new ArraySegment<byte> (buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Manager.Users.TryRemove(UserId.ToString(), out _);
                await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                websocket.Dispose();
                break;
            } else
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var ResponseBytes = Encoding.UTF8.GetBytes($"Echo {message}");
                await websocket.SendAsync(new ArraySegment<byte> (ResponseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }   
        }
    }
}