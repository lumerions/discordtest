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

        try {

        Manager.Users[UserId.ToString()] = websocket;
        var buffer = new byte[1024  * 4];

        while (websocket.State == WebSocketState.Open)
        {
            var result = await websocket.ReceiveAsync(new ArraySegment<byte> (buffer), CancellationToken.None);
            var exit = false;

            switch (result.MessageType)
            {
                case WebSocketMessageType.Text:
                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    switch (text)
                    {
                        case "Typing":

                        break;
                    }

                    break;
                case WebSocketMessageType.Close:
                    await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    websocket.Dispose();
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