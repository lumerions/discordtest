using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Internal.Redis;
using StackExchange.Redis;
using Internal.Shared;
using System.Linq;
using System.Collections.Concurrent;
using Internal.Main;
using Microsoft.AspNetCore.RateLimiting;
using Controllers.ControllBase;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Internal.MainController;

public class ConnectionsChangeRequest
{
    public bool Visible {get; set;}
}


public class TypingRequest
{
    public int DiscordChannelId {get; set;}
}

public class YoutubeChannelResponse
{
    [JsonPropertyName("items")]
    public List<YoutubeChannel> Items { get; set; } = [];
}

public class YoutubeChannel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("snippet")]
    public YoutubeChannelSnippet Snippet { get; set; } = new();
}

public class YoutubeChannelSnippet
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("customUrl")]
    public string? CustomUrl { get; set; }
}

public class YoutubeCallbackResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken  {get; set;}
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

[ApiController]
[Route("/api/")]
public class MainController : BaseController
{
    private readonly RedisHandler Redis;
    private readonly IDatabase RedisDatabase;
    private readonly SharedMethods.ServerIdUserIdConnections ServerIdIds;

    private readonly SharedMethods.WebSocketChannelIdConnections WebSocketChannelIds;
    private readonly IHttpClientFactory factory;
    private readonly IConfiguration config;

    private readonly MainHandler mainhandler;

    public MainController(MainHandler mainhandler_, IConfiguration config_, IHttpClientFactory factory_, RedisHandler redis_, SharedMethods.WebSocketChannelIdConnections WebSocketChannelIds_, SharedMethods.ServerIdUserIdConnections ServerIdIds_)
    {
        Redis = redis_;
        RedisDatabase = redis_.GetRedisDatabase();
        WebSocketChannelIds = WebSocketChannelIds_;
        ServerIdIds = ServerIdIds_;
        factory = factory_;
        config = config_;
        mainhandler = mainhandler_;
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("GetTypingUsers")]
    public async Task<List<string>> GetTypingUsers ([FromBody] TypingRequest request)
    {
        var TypingUsers = (await RedisDatabase.SetMembersAsync($"channel:{request.DiscordChannelId}")).Take(5).Select(x => (string) x).ToList();
        return TypingUsers;
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("ChannelInfo")]
    public async Task<IActionResult> ChannelInfo ([FromBody] TypingRequest request)
    {
        var channelId = request.DiscordChannelId.ToString();

        if (UserId == null) return BadRequest("UserId doesn't exist.");

        var Users = WebSocketChannelIds.ChannelUsers.GetOrAdd(channelId, _ => new ConcurrentDictionary<string,byte>());

        Users.TryAdd(UserId.ToString(), 0);

        var ServerIdUsers = ServerIdIds.ServerIdUsers.GetOrAdd(channelId, _ => new ConcurrentDictionary<string,byte>());

        ServerIdUsers.TryAdd(UserId.ToString(), 0);

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("ConnectionVisibility")]
    public async Task<IActionResult> SetConnectionVisibility ([FromBody] ConnectionsChangeRequest request)
    {
        var ConnectionVisible = request.Visible;
        var Id = 0;

        if (UserId == null) return BadRequest("UserId doesn't exist.");

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await mainhandler.UpdateConnections(Id, "no", "no", "no", "no", ConnectionVisible);

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("oauth/start/youtube")]
    public async Task<IActionResult> OauthStart ([FromBody] TypingRequest request)
    {
        var YoutubeClientId = config["Main:YTCI"];
        var Code = RandomNumberGenerator.GetHexString(16);
        var ConstructedUrl = mainhandler.GetYoutubeOauthLink(YoutubeClientId!, "test", Code);
        var Id = 0;

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await mainhandler.UpdateConnections(Id, "no", "no", "no", "notset", null);

        return Ok(new
        {
            url = ConstructedUrl,
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpGet("oauth/youtube/callback")]
    public async Task<IActionResult> YoutubeCallback ([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        var Id = 0;
        if (!string.IsNullOrEmpty(error))
        {
            return BadRequest("Error with callback");
        }
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Invalid or expired code.");
        }
        if (string.IsNullOrEmpty(state))
        {
            return BadRequest("Invalid or expired state.");
        }

        if (!GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var StateValid = await mainhandler.StateValid(state, Id);

        if (!StateValid)
        {
            return Unauthorized();
        }

        var YoutubeClientId = config["Main:YTCI"];
        var YoutubeClientSecret = config["Main:YTCS"];
        var YoutubeRedirectUrl = config["Main:YRU"];

        HttpClient HttpCliente = factory.CreateClient();

        var TokenResponse = await HttpCliente.PostAsync(
        "https://oauth2.googleapis.com/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = YoutubeClientId!,
            ["client_secret"] = YoutubeClientSecret!,
            ["redirect_uri"] = YoutubeRedirectUrl!,
            ["grant_type"] = "authorization_code"
        }));

        if (!TokenResponse.IsSuccessStatusCode)
        {
            var ErrorMessage = await TokenResponse.Content.ReadAsStringAsync();
            return BadRequest(ErrorMessage);
        }

        var TokenReply = await TokenResponse.Content.ReadFromJsonAsync<YoutubeCallbackResponse>();

        if (TokenReply == null)
        {
            return BadRequest("Error getting callback.");
        }

        var AccessToken = TokenReply.AccessToken;
        //var ExpiresIn = TokenReply.ExpiresIn;
        var RefreshToken = TokenReply.RefreshToken;

        var YTChannelInfo = await GetYoutubeChannel(AccessToken);

        if (YTChannelInfo == null)
        {
            return BadRequest("Could not find channel.");
        }

        var ChannelId = YTChannelInfo.Id;
        var YTChannelUrl = $"https://www.youtube.com/channel/{ChannelId}";

        var ChannelName = YTChannelInfo?.Snippet?.Title;

        await mainhandler.UpdateConnections(Id, ChannelName, AccessToken, RefreshToken, YTChannelUrl, null);

        return Ok(new
        {
            success = true,
        });
    }

    public async Task<YoutubeChannel?> GetYoutubeChannel (string AccessToken)
    {
        HttpClient HttpCliente = factory.CreateClient();

        HttpCliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await HttpCliente.GetAsync(
        "https://www.googleapis.com/youtube/v3/channels" +
        "?part=snippet" +
        "&mine=true");

        response.EnsureSuccessStatusCode();

        var TokenReply = await response.Content.ReadFromJsonAsync<YoutubeChannelResponse>();

        if (TokenReply == null)
        {
            return null;
        }

        return TokenReply?.Items.FirstOrDefault();
    }
}