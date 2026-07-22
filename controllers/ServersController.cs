using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Internal.Servers;
using System.ComponentModel.DataAnnotations;

public record JoinServerDto (
    [Required] Guid ServerId,
    [Required] Guid InviteCode
);

[ApiController]
[Route("/internal/servers/")]
public class ServersController : ControllerBase
{
    private readonly Server ServerHandler;
    public ServersController(Server ServerHandler_)
    {
        ServerHandler = ServerHandler_;
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
}