
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;

[ApiController]
[Route("/auth/{controller}")]
public class AuthenicationController : ControllerBase
{
    public JwtSecurityToken? GetJWTValue(string Token)
    {
        var handler = new JwtSecurityTokenHandler();

        if (string.IsNullOrWhiteSpace(Token))
        {
            return null;
        }
        
        if (handler.CanReadToken(Token))
        {
            JwtSecurityToken jwtToken = handler.ReadJwtToken(Token);
            return jwtToken;
        }

        return null;
    }

    public string SetJWTValue(IConfiguration configuration, long UserId, string Email, string Username)
    {
        var JWTKey = configuration["Main:JWTKEY"];
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JWTKey!));
        var securityCreds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, Username.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(30),
            Issuer = "Discordia",
            Audience = "Discordians",
            SigningCredentials = securityCreds
        };

        var handler = new JwtSecurityTokenHandler();
        var tokenobj = handler.CreateToken(tokenDescriptor);

        return handler.WriteToken(tokenobj);
    }
}