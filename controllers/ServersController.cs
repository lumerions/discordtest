using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.AspNetCore.Authorization;
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
using Controllers.ControllBase;

namespace Internal.ServerCont;

public abstract record ServerIdBase
{
    [Required]
    public required Guid ServerId {get; init;}
}

public abstract record ServerIdChannelIdBase : ServerIdBase
{
    [Required]
    public required Guid ChannelId {get; init;}
}

public record RevokeInviteDto : ServerIdBase
{
    [Required]
    public required string InviteCode {get; init;}
}

public record InviteDto : ServerIdChannelIdBase
{
    [Required]
    public required string ExpiresAt {get; init;}
    public required int MaxUses {get; init;}
}

public record NewChannel : ServerIdBase
{
    [Required]
    public required string ChannelName {get; init;}
    public required string ChannelType {get; init;}
    public required string ChannelTopic {get; init;}
    public required int Position {get; init;}
}

public record ChangeNickname : ServerIdBase
{
    [Required]
    public required string NewNickname {get; init;}
    public required int UserId {get; init;}
}

public record KickOrLeave : ServerIdBase
{
    [Required]

    public required bool Kick {get; init;}
    public required int UserId {get; init;}
}

public record CreateServerDto
{
    [Required]
    public required string ServerName {get; init;}
}

public record DeleteServerDto : ServerIdBase {};

public record JoinServerDto : ServerIdChannelIdBase
{
    [Required]
    public required string InviteCode {get; init;}
}

public record BanOrMuteDto : ServerIdBase
{
    [Required]
    public required string BanUsername {get; init;}
    public required int BanId {get; init;}
    public required DateTime ExpiresAt {get; init;}
    public required string ModerationAction {get; init;}
    public required string? BanReason {get; init;}
}

public record ChangeIdWebhookDto : ServerIdChannelIdBase
{
    [Required]
    public required Guid WebhookId {get; init;}
}

public record SendWebhookMessageDto
{
    [Required]
    public required string WebhookMessage {get; init;}
    public required Guid WebhookId {get; init;}
}

public record CreateChannelWebhook : ServerIdChannelIdBase {};

[ApiController]
[Route("/api/internal/servers/")]
public class ServersController : BaseController
{
    private readonly Server ServerHandler;
    private readonly IDatabase RedisDatabase;
    private readonly SharedMethods.WebSocketSessionManager Manager;

    private readonly SharedMethods.WebSocketChannelIdConnections websocketconns_;

    public async Task<(Permissions? Perm, bool Successful, Dictionary<string, string> ChannelIds)> GetPerm (Guid ServerId, int IdValue, bool PermissionCheck)
    {
        if (!PermissionCheck) return (null, false, new Dictionary<string, string>());

        var PermissionInfo = await ServerHandler.GetChannelIdsByServerId(ServerId, IdValue, PermissionCheck);

        if (!PermissionInfo.ContainsKey("Permissions")) {
            return (null, false, PermissionInfo);
        }
        
        string PermissionString = PermissionInfo.GetValueOrDefault("Permissions");
        long PermissionNumber = long.Parse(PermissionString);
        var Perm = (Permissions) PermissionNumber;

        return (Perm, true, PermissionInfo);
    }

    public bool GetIdValue (ref int IdVar)
    {
        if (string.IsNullOrWhiteSpace(UserId)) return false;
        if (string.IsNullOrWhiteSpace(UserName)) return false;

        if (int.TryParse(UserId, out var IdValue))
        {
            IdVar = IdValue;
            return true;
        }

        return false;
    }

    public ServersController (SharedMethods.WebSocketSessionManager manager, RedisHandler redis_, Server ServerHandler_, SharedMethods.WebSocketChannelIdConnections  websocketconns)
    {
        ServerHandler = ServerHandler_;
        websocketconns_ = websocketconns;
        RedisDatabase = redis_.GetRedisDatabase();
        Manager = manager;
    }

    public async Task SendUpdate (WebSocket socket, string UserIdToRemove, string? JsonContent)
    {   
        string MessageUpdateType = "";
        if (JsonContent == null)
        {
            MessageUpdateType = $"remove_user:{UserIdToRemove}";
        } else
        {
            MessageUpdateType = JsonContent;
        }

        var MessageUpdateTypeBytes = Encoding.UTF8.GetBytes(MessageUpdateType);
        var MessageUpdateTypeBuffer = new ArraySegment<byte> (MessageUpdateTypeBytes);

        try {

            if (socket.State != WebSocketState.Open)
            {
                Manager.Users.TryRemove(UserIdToRemove, out _);
                socket.Dispose();
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
                Manager.Users.TryRemove(UserIdToRemove, out _);
                socket.Dispose();
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
                            Tasks.Add(SendUpdate(UserSocket, UserId.ToString(), null));
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

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("joinServer")]
    public async Task<IActionResult> UserJoin ([FromBody] JoinServerDto request)
    {
        var ServerId = request.ServerId;
        var InviteCode = request.InviteCode;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var JoinServerResult = await ServerHandler.JoinServer(ServerId, Id, UserName, InviteCode);

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

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("moderation-action")]
    public async Task<IActionResult> ModerationAction ([FromBody] BanOrMuteDto request)
    {
        var ServerId = request.ServerId;
        var BanId = request.BanId;
        var BanUsername = request.BanUsername;
        var BanReason = request.BanReason;
        var ExpiresAt = request.ExpiresAt;
        var ModerationAction = request.ModerationAction;
        int Id = 0;

        if (ModerationAction != "server_mutes" && ModerationAction != "server_bans")
        {
            return BadRequest("Invalid moderation action.");
        }

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        if (BanReason == null) BanReason = "";

        var PermissionResult = await GetPerm(ServerId, Id, true);
        var Perm = PermissionResult.Perm;
        
        if (Perm == null) 
        { 
            return BadRequest();
        }

        var ChannelIds = PermissionResult.ChannelIds;

        if (ModerationAction == "server_mutes")
        {
            var CanMute = (Perm & Permissions.TimeoutMembers) != 0;

            if (!CanMute)
            {
                return Unauthorized();
            }

            await ServerHandler.BanOrMuteUser(ServerId, BanId, Id, BanReason, ExpiresAt, ModerationAction);
            await SetTypingStatus(ChannelIds, BanUsername, BanId, null);
        }

        if (ModerationAction == "server_bans")
        {
            var CanBan = (Perm & Permissions.BanMembers) != 0;

            if (!CanBan)
            {
                return Unauthorized();
            }

            await ServerHandler.BanOrMuteUser(ServerId, BanId, Id, BanReason, ExpiresAt, ModerationAction);
            await SetTypingStatus(ChannelIds, BanUsername, BanId, true);
        }
            
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("delete-server")]
    public async Task<IActionResult> DeleteServer ([FromBody] DeleteServerDto request)
    {
        var ServerId = request.ServerId;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await ServerHandler.DeleteGuild(ServerId, Id);
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("create-server")]
    public async Task<IActionResult> CreateServer ([FromBody] CreateServerDto request)
    {
        var ServerName = request.ServerName;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await ServerHandler.CreateNewServer(ServerName, Id, UserName);
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("kick-leave-server")]
    public async Task<IActionResult> KickOrLeaveMember ([FromBody] KickOrLeave request)
    {
        var KickMember = request.Kick;
        var KickUserId = request.UserId;
        var ServerId = request.ServerId;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var PermissionInfo = await ServerHandler.GetChannelIdsByServerId(ServerId, Id, true);

        if (KickMember)
        {
            if (!PermissionInfo.ContainsKey("Permissions")) {
                return BadRequest();
            }
            
            string PermissionString = PermissionInfo.GetValueOrDefault("Permissions");
            long PermissionNumber = long.Parse(PermissionString);
            var Perm = (Permissions) PermissionNumber;
            var canKick = (Perm & Permissions.KickMembers) != 0;

            if (!canKick)
            {
                return Unauthorized();
            }
        }

        await ServerHandler.KickUser(ServerId, KickUserId);
    
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("change-nickname")]
    public async Task<IActionResult> ChangeNickname ([FromBody] ChangeNickname request)
    {
        var NewNickname = request.NewNickname;
        var NewNicknameId = request.UserId;
        var ServerId = request.ServerId;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var PermissionResult = await GetPerm(ServerId, Id, true);
        var Perm = PermissionResult.Perm;
        
        if (Perm == null) 
        { 
            return BadRequest();
        }

        if (NewNicknameId == Id)
        {
            var CanChangePersonalNickname = (Perm & Permissions.ChangeNickname) != 0;

            if (!CanChangePersonalNickname)
            {
                return Unauthorized();
            }
        } else
        {
            var CanManageNicknames = (Perm & Permissions.ManageNicknames) != 0;

            if (!CanManageNicknames)
            {
                return Unauthorized();
            }
        }

        await ServerHandler.ChangeServerNickname(ServerId, NewNicknameId, NewNickname);
        // websocket support needs to be added for all of this but ill do it later
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("new-channel")]
    public async Task<IActionResult> NewServerChannel ([FromBody] NewChannel request)
    {
        var ChannelType = request.ChannelType;
        var ChannelName = request.ChannelName;
        var ChannelPosition = request.Position;
        var ServerId = request.ServerId;
        var ChannelTopic = request.ChannelTopic;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        if (ChannelType != "text" && ChannelType != "voice" && ChannelType != "category") 
        {
            return BadRequest("Invalid channel type.");
        }

        var PermissionResult = await GetPerm(ServerId, Id, true);
        var Perm = PermissionResult.Perm;
        
        if (Perm == null) 
        { 
            return BadRequest();
        }

        var CanManageChannels = (Perm & Permissions.ManageChannels) != 0;

        if (!CanManageChannels)
        {
            return Unauthorized();
        }

        await ServerHandler.CreateServerChannel(ServerId, ChannelType, ChannelPosition, ChannelName, ChannelTopic);
        // websocket support needs to be added for all of this but ill do it later
    
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("invite-members")]
    public async Task<IActionResult> NewInvite ([FromBody] InviteDto request)
    {
        var ChannelId = request.ChannelId;
        var ServerId = request.ServerId;
        var MaxUses = request.MaxUses;
        var Expiration = request.ExpiresAt;
        int Id = 0;

        if (Expiration != "30d" && Expiration != "Never" && Expiration != "7d" && Expiration != "1d" && Expiration != "12h" && Expiration != "6h" && Expiration != "1h" && Expiration != "30m")
        {
            return BadRequest("Invalid expiration time.");
        }

        if (MaxUses != 1 && MaxUses != 5 && MaxUses != 10 && MaxUses != 25 && MaxUses != 50 && MaxUses != 100)
        {
            return BadRequest("Invalid Max Uses.");
        }

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        if (int.TryParse(UserId, out var IdValue))
        {
            var PermissionResult = await GetPerm(ServerId, IdValue, true);
            var Perm = PermissionResult.Perm;
          
            if (Perm == null) 
            { 
                return BadRequest();
            }

            var CanInviteUsers = (Perm & Permissions.CreateInvites) != 0;

            if (!CanInviteUsers)
            {
                return Unauthorized();
            }
            
            await ServerHandler.CreateNewServerInvite(ServerId, IdValue, MaxUses, ChannelId, Expiration);
        }
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("revoke-invite")]
    public async Task<IActionResult> RevokeInvite ([FromBody] RevokeInviteDto request)
    {
        var ServerId = request.ServerId;
        var InviteCode = request.InviteCode;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var PermissionResult = await GetPerm(ServerId, Id, true);
        var Perm = PermissionResult.Perm;
        
        if (Perm == null) 
        { 
            return BadRequest();
        }

        var CanRevokeInvites = (Perm & Permissions.Administrator) != 0;

        if (!CanRevokeInvites)
        {
            return Unauthorized();
        }
        
        await ServerHandler.RevokeInvite(ServerId, InviteCode);
    
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("change-channel-webhook-id")]
    public async Task<IActionResult> ChangeChannelId ([FromBody] ChangeIdWebhookDto request)
    {
        var ServerId = request.ServerId;
        var WebhookId = request.WebhookId;
        var ChannelId = request.ChannelId;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }
            
        await ServerHandler.ChangeChannelIdWebhook(ChannelId, ServerId, Id, WebhookId);
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("channel-webhook-create")]
    public async Task<IActionResult> ChannelWebhook ([FromBody] CreateChannelWebhook request)
    {
        var ChannelId = request.ChannelId;
        var ServerId = request.ServerId;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await ServerHandler.AddChannelWebhook(ChannelId, ServerId, Id);
        
        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("send-webhook-message")]
    public async Task<IActionResult> WebhookMessage ([FromBody] SendWebhookMessageDto request)
    {
        var WebhookId = request.WebhookId;
        var WebhookMessage = request.WebhookMessage;
        int Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await ServerHandler.SendChannelWebhookMessage(WebhookId, WebhookMessage);

        return Ok(new
        {
            success = true
        });
    }
}