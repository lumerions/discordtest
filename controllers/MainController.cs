using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Internal.Redis;
using StackExchange.Redis;
using Internal.Shared;
using System.Linq;
using System.Collections.Concurrent;

namespace Internal.MainController;
public class TypingRequest
{
    public int DiscordChannelId {get; set;}
}

[ApiController]
[Route("/api")]
public class MainController : ControllerBase
{
    private readonly RedisHandler Redis;
    private readonly IDatabase RedisDatabase;

    private readonly SharedMethods.WebSocketChannelIdConnections WebSocketChannelIds;

    public MainController(RedisHandler redis_, SharedMethods.WebSocketChannelIdConnections WebSocketChannelIds_)
    {
        Redis = redis_;
        RedisDatabase = redis_.GetRedisDatabase();
        WebSocketChannelIds = WebSocketChannelIds_;
    }

    [HttpPost("/GetTypingUsers")]
    public async Task<List<string>> GetTypingUsers(TypingRequest request)
    {
        var TypingUsers = (await RedisDatabase.SetMembersAsync($"channel:{request.DiscordChannelId}")).Take(5).Select(x => (string) x).ToList();
        return TypingUsers;
    }

    [HttpPost("/ChannelInfo")]
    public async Task<IActionResult> ChannelInfo([FromBody] TypingRequest request)
    {
        var UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var channelId = request.DiscordChannelId.ToString();

        if (UserId == null) return BadRequest("UserId doesn't exist.");

        var Users = WebSocketChannelIds.ChannelUsers.GetOrAdd(channelId, _ => new ConcurrentDictionary<string,byte>());

        Users.TryAdd(UserId.ToString(), 0);
        
        // TODO
        return Ok(new
        {
            success = true
        });
    }
}