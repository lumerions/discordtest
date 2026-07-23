using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.AspNetCore.RateLimiting;
using Internal.Servers;
using System.ComponentModel.DataAnnotations;
using Internal.Shared;
using Internal.Redis;
using Internal.Roles;
using System.Text;
using System.Net.WebSockets;
using System.Linq;
using StackExchange.Redis;
using System.Collections.Concurrent;

public record JoinServerDto (
    [Required] Guid ServerId,
    [Required] Guid InviteCode
);

public record BanOrMuteDto (
    [Required] Guid ServerId,
    [Required] string BanUsername,
    [Required] int BanId, 
    [Required] DateTime ExpiresAt, 
    [Required] string ModerationAction,
    string? BanReason
);

[ApiController]
[Route("/internal/servers/")]
public class ServersController : ControllerBase
{
    private readonly Server ServerHandler;
    private readonly IDatabase RedisDatabase;
    private readonly SharedMethods.WebSocketSessionManager Manager;

    private readonly SharedMethods.WebSocketChannelIdConnections websocketconns_;

    public ServersController (SharedMethods.WebSocketSessionManager manager, RedisHandler redis_, Server ServerHandler_, SharedMethods.WebSocketChannelIdConnections  websocketconns)
    {
        ServerHandler = ServerHandler_;
        websocketconns_ = websocketconns;
        RedisDatabase = redis_.GetRedisDatabase();
        Manager = manager;
    }

    public async Task SendUpdate (WebSocket socket, string UserIdToRemove)
    {
        var MessageUpdateType = $"remove_user:{UserIdToRemove}";
        var MessageUpdateTypeBytes = Encoding.UTF8.GetBytes(MessageUpdateType);
        var MessageUpdateTypeBuffer = new ArraySegment<byte> (MessageUpdateTypeBytes);

        try {

            if (socket.State != WebSocketState.Open)
            {
                socket.Dispose();
                Manager.Users.TryRemove(UserIdToRemove, out _);
                return;
            }

            await socket.SendAsync(MessageUpdateTypeBuffer, WebSocketMessageType.Text, true, CancellationToken.None);

        } catch
        {
            try {
                if (socket.State == WebSocketState.CloseReceived || socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Internal Socket Error.", CancellationToken.None);
                }
            } finally
            {
                socket.Dispose();
                Manager.Users.TryRemove(UserIdToRemove, out _);
            }
        }
    }

    public async Task SetTypingStatus (Dictionary<string, string> ChannelIds, string BanUsername, int BanId, bool? BannedUser)
    {
        var ChannelIdData = ChannelIds.ToDictionary(x => x.Key, x => x.Value);
        ChannelIdData.Remove("Permissions");

        foreach (var item in ChannelIdData.Keys.ToList())
        {
            var fullKey = item + ";" + BanUsername + ";" + BanId.ToString();

            if (BannedUser == true) 
            {
                var Tasks = new List<Task>();
                if (websocketconns_.ChannelUsers.TryGetValue(item, out var UserIds))
                {
                    foreach (var UserId in UserIds)
                    {
                        if (Manager.Users.TryGetValue(UserId.ToString(), out var UserSocket))
                        {
                            Tasks.Add(SendUpdate(UserSocket, UserId.ToString()));
                        }
                    }
                }

                await Task.WhenAll(Tasks);
            }

            ChannelIdData[item] = fullKey;
        }

        var RedisBulkOps = ChannelIdData.Select(item => new KeyValuePair<RedisKey, RedisValue>(item.Key, item.Value)).ToArray();
        await RedisDatabase.StringSetAsync(RedisBulkOps);
    }

    [EnableRateLimiting("api")]
    [HttpPost("joinServer")]
    public async Task<IActionResult> UserJoin ([FromBody] JoinServerDto request)
    {
        var ServerId = request.ServerId;
        var InviteCode = request.InviteCode;
        var UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var UserName = User.FindFirst(ClaimTypes.Name)?.Value;

        if (string.IsNullOrWhiteSpace(UserId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(UserName)) return Unauthorized();

        if (int.TryParse(UserId, out var UserIdInt))
        {
            var JoinServerResult = await ServerHandler.JoinServer(ServerId, UserIdInt, UserName, InviteCode);

            if (!JoinServerResult.Contains("Successfully"))
            {
                return BadRequest(new
                {
                    message = JoinServerResult
                });
            }

            return Ok(new
            {
                success = true
            });
        }

        return BadRequest("Could not join server please try again later.");
    }

    [EnableRateLimiting("api")]
    [HttpPost("moderation-action")]
    public async Task<IActionResult> ModerationAction ([FromBody] BanOrMuteDto request)
    {
        var UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(UserId)) return Unauthorized();

        var ServerId = request.ServerId;
        var BanId = request.BanId;
        var BanUsername = request.BanUsername;
        var BanReason = request.BanReason;
        var ExpiresAt = request.ExpiresAt;
        var ModerationAction = request.ModerationAction;

        if (ModerationAction != "server_mutes" && ModerationAction != "server_bans")
        {
            return BadRequest("Invalid moderation action.");
        }

        if (int.TryParse(UserId, out var UserIdInt)) {

            var ChannelIds = await ServerHandler.GetChannelIdsByServerId(ServerId, UserIdInt, ModerationAction == "server_bans");

            if (ChannelIds.ContainsKey("Permissions")) {
                return BadRequest();
            }
            
            string PermissionString = ChannelIds.GetValueOrDefault("Permissions");
            long PermissionNumber = long.Parse(PermissionString);
            var Perm = (Permissions) PermissionNumber;

            if (ModerationAction == "server_mutes")
            {
                var CanMute = (Perm & Permissions.TimeoutMembers) != 0;

                if (!CanMute)
                {
                    return Unauthorized();
                }

                await ServerHandler.BanOrMuteUser(ServerId, BanId, UserIdInt, BanReason, ExpiresAt, ModerationAction);
                await SetTypingStatus(ChannelIds, BanUsername, BanId, null);
            }

            if (ModerationAction == "server_bans")
            {
                var CanBan = (Perm & Permissions.BanMembers) != 0;

                if (!CanBan)
                {
                    return Unauthorized();
                }

                await ServerHandler.BanOrMuteUser(ServerId, BanId, UserIdInt, BanReason, ExpiresAt, ModerationAction);
                await SetTypingStatus(ChannelIds, BanUsername, BanId, true);
            }
            
            return Ok(new
            {
                success = true
            });
        }

        return BadRequest("Error getting userid.");
    }
}