using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Npgsql;
using Internal.Data;
using Internal.Database;
using Internal.Authenication;

public record RegisterDto (
    [Required] [EmailAddress] string Email,
    [Required] string Password,
    [Required] string Username,
    [Required] string Day,
    [Required] string Month,
    [Required] string Year
);


public record LoginDto (
    [Required] string Email,
    [Required] string Password
);

[ApiController]
[Route("internal/account/")]
public class AccountController : ControllerBase
{
    private readonly DataHandler datahandler;
    private readonly IConfiguration configuration;
    private readonly DatabaseHandler DBHandler;
    private readonly AuthenicationController Authenication;
    public AccountController (DataHandler datahandler_, IConfiguration configuration_, DatabaseHandler DBHandler_, AuthenicationController Authenication_)
    {
        datahandler = datahandler_;
        configuration = configuration_;
        DBHandler = DBHandler_;
        Authenication = Authenication_;
    }

    [EnableRateLimiting("api")]
    [HttpPost("login")]
    public async Task<IActionResult> Login ([FromBody] LoginDto request)
    {
        var Email = request.Email;
        var Password = request.Password;
        var SecretKey = configuration["Main:HMacSha256Key"];
        var EncryptKey = configuration["Main:EncryptionKey"];

        if (string.IsNullOrEmpty(SecretKey))
        {
            return BadRequest("SecretHmac Key missing.");
        }

        if (string.IsNullOrEmpty(EncryptKey))
        {
            return BadRequest("EncryptKey Key missing.");
        }

        if (Password.Length < 8 || Password.Length > 128)
        {
            return BadRequest("Invalid password length must be > 8 or < 128.");
        }

        if (Email.Length > 320) 
        {
            return BadRequest("Email address is too long.");
        }

        byte[] SecretKeyBytes = Convert.FromBase64String(SecretKey);
        var EmailHmacSha256 = datahandler.HmacSha256(Email, SecretKeyBytes);

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT password_hash, is_banned, id, username, ciphertext, tag, nonce FROM users WHERE email_lookup = @email_lookup;",conn);
        cmd.Parameters.AddWithValue("email_lookup", EmailHmacSha256);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized("Invalid username or password.");
        }

        var PasswordHash = reader.GetString(0);
        var Banned = reader.GetInt32(1);
        var UserId = reader.GetInt32(2);
        var Username = reader.GetString(3);
        byte[] ciphertext = reader.GetFieldValue<byte[]>(4);
        byte[] tag = reader.GetFieldValue<byte[]>(5);
        byte[] nonce = reader.GetFieldValue<byte[]>(6);

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        if (!datahandler.VerifyArgonHash(Password, PasswordHash))
        {
            return Unauthorized("Invalid username or password.");
        }

        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey);
        var UserEmail = datahandler.Decrypt(ciphertext, nonce, tag, EncryptKeyBytes);
        var Token = Authenication.SetJWTValue(configuration, UserId, UserEmail, Username);

        Response.Cookies.Append("jwt", Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            MaxAge = TimeSpan.FromDays(30)
        });
        
        return Ok(new
        {
            success = true
        });
    }

    [EnableRateLimiting("api")]
    [HttpPost("register")]
    public async Task<IActionResult> Register ([FromBody] RegisterDto request)
    {
        var Email = request.Email;
        var Password = request.Password;
        var Username = request.Username.Trim();
        var DayBorn = request.Day;
        var MonthBorn = request.Month;
        var YearBorn = request.Year;
        var SecretKey = configuration["Main:HMacSha256Key"];
        var EncryptKey = configuration["Main:EncryptionKey"];

        if (string.IsNullOrEmpty(SecretKey))
        {
            return BadRequest("SecretHmac Key missing.");
        }

        if (string.IsNullOrEmpty(EncryptKey))
        {
            return BadRequest("EncryptKey Key missing.");
        }

        if (Password.Length < 8 || Password.Length > 128)
        {
            return BadRequest("Invalid password length must be > 8 or < 128.");
        }

        if (Email.Length > 320) 
        {
            return BadRequest("Email address is too long.");
        }

        var DOBString = $"{DayBorn}/{MonthBorn}/{YearBorn}";
        byte[] SecretKeyBytes = Convert.FromBase64String(SecretKey);
        var EmailHmacSha256 = datahandler.HmacSha256(Email, SecretKeyBytes);
        var PasswordHash = datahandler.ArgonHash(Password);
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey);
        var EncryptionResult = datahandler.Encrypt(Email, EncryptKeyBytes);
        var nonce = EncryptionResult.nonce;
        var ciphertext = EncryptionResult.ciphertext;
        var tag = EncryptionResult.tag;

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO users (
                email_lookup,
                username,
                display_name,
                dob,
                password_hash,
                nonce,
                tag,
                ciphertext
            )
            VALUES (
                @email_lookup,
                @username,
                @username,
                @dob,
                @password_hash,
                @nonce,
                @tag,
                @ciphertext
            )
            RETURNING id;
            """, conn);

        cmd.Parameters.AddWithValue("email_lookup", EmailHmacSha256);
        cmd.Parameters.AddWithValue("dob", DOBString);
        cmd.Parameters.AddWithValue("username", Username);
        cmd.Parameters.AddWithValue("password_hash", PasswordHash);
        cmd.Parameters.AddWithValue("nonce", nonce);
        cmd.Parameters.AddWithValue("ciphertext", ciphertext);
        cmd.Parameters.AddWithValue("tag", tag);

        var Result = await cmd.ExecuteScalarAsync();

        if (Result == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        var UserId = Convert.ToInt32(Result);
        var Token = Authenication.SetJWTValue(configuration, UserId, Email, Username);

        Response.Cookies.Append("jwt", Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            MaxAge = TimeSpan.FromDays(30)
        });
        
        return Ok(new
        {
            success = true
        });
    }
}