using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Internal.Redis;
using StackExchange.Redis;
using System.Linq;

namespace Internal.MainController;
public class TypingRequest
{
    public int DiscordChannelId {get; set;}
}

[ApiController]
[Route("/api")]
public class MainController
{
    private readonly RedisHandler Redis;
    private readonly IDatabase RedisDatabase;

    public MainController(RedisHandler redis_)
    {
        Redis = redis_;
        RedisDatabase = redis_.GetRedisDatabase();
    }

    [HttpGet("/GetTypingUsers")]
    public async Task<List<string>> GetTypingUsers(TypingRequest request)
    {
        var TypingUsers = (await RedisDatabase.SetMembersAsync($"channel:{request.DiscordChannelId}")).Take(5).Select(x => (string) x).ToList();
        return TypingUsers;
    }
}