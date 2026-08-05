using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using System;
using System.Security.Claims;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Npgsql;
using UAParser;
using Internal.Data;
using Internal.Database;
using Internal.Authenication;
using Internal.Accounts;
using System.Security.Cryptography;
using Controllers.ControllBase;

public class IpApiResponse
{
    public string country { get; set; }
    public string regionName { get; set; }
    public string city { get; set; }
}

public abstract record RegisterLoginBase
{
    [Required]
    [EmailAddress]
    public required string Email {get; init;}
}

public abstract record AccountsBase
{
    [Required]
    public required string Password {get; init;}
}

public record ChangePasswordDto 
{
    [Required]
    public required string CurrentPassword {get; init;}
    public required string NewPassword {get; init;}

}

public record RegisterDto : RegisterLoginBase
{
    [Required]
    public required string Password {get; init;}
    public required string Username {get; init;}
    public required string Day {get; init;}
    public required string Month {get; init;}
    public required string Year {get; init;}

}

public record LoginDto : RegisterLoginBase
{
    [Required]
    public required string Password {get; init;}
}

[ApiController]
[Route("/api/internal/account/")]
public class AccountController : BaseController
{
    private readonly DataHandler datahandler;
    private readonly IConfiguration configuration;
    private readonly DatabaseHandler DBHandler;
    private readonly AuthenicationController Authenication;
    private readonly IHttpClientFactory HttpClientfactory;
    private readonly AccountHandler Accounts;
    public AccountController (AccountHandler Accounts_, IHttpClientFactory HttpClientfactory_, DataHandler datahandler_, IConfiguration configuration_, DatabaseHandler DBHandler_, AuthenicationController Authenication_)
    {
        datahandler = datahandler_;
        configuration = configuration_;
        DBHandler = DBHandler_;
        Authenication = Authenication_;
        HttpClientfactory = HttpClientfactory_;
        Accounts = Accounts_;
    }

    public async Task<string> GetLocationString (string IPAddress) 
    {
        var Client = HttpClientfactory.CreateClient();

        try {
            var Response = await Client.GetStringAsync($"http://ip-api.com/json/{IPAddress}");

            if (string.IsNullOrEmpty(Response))
            {
                throw new Exception ("Invalid response");
            }

            var JsonObject = JsonSerializer.Deserialize<IpApiResponse>(Response);

            if (JsonObject == null)
            {
                throw new Exception ("Invalid response");
            }

            var RegionName = JsonObject.regionName;
            var Country = JsonObject.country;
            var City = JsonObject.city;
            var LocationString = $"{City},{RegionName},{Country}";

            return LocationString;
        } catch (Exception err) {
            Console.WriteLine(err);
            return "Unknown Location";
        }
    }

    public (string OS, string Browser, string IP) GetUserInfo ()
    {
        var UserIPAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        if (string.IsNullOrEmpty(UserIPAddress)) UserIPAddress = "8.8.8.8";

        var UserAgent = HttpContext.Request.Headers["User-Agent"].ToString();
        var parser = Parser.GetDefault();
        var clientInfo = parser.Parse(UserAgent);
        var OS = clientInfo.OS.Family;
        var Browser = clientInfo.UA.Family;
        return (OS, Browser, UserIPAddress);
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
        var UserInfo = GetUserInfo();
        var IPAddress = UserInfo.IP;
        var OperatingSys = UserInfo.OS;
        var Browser = UserInfo.Browser;
        var Location = await GetLocationString(IPAddress);
        var CsrfToken = RandomNumberGenerator.GetHexString(32);

        Response.Cookies.Append("jwt", Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });

        Response.Cookies.Append("x-csrf-token", CsrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });

        await Accounts.CreateNewSession(OperatingSys, Browser, Location, UserId, Token);

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
        var UserInfo = GetUserInfo();
        var IPAddress = UserInfo.IP;
        var OperatingSys = UserInfo.OS;
        var Browser = UserInfo.Browser;
        var Location = await GetLocationString(IPAddress);
        var CsrfToken = RandomNumberGenerator.GetHexString(32);

        Response.Cookies.Append("jwt", Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });

        Response.Cookies.Append("x-csrf-token", CsrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(30),
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });

        await Accounts.CreateNewSession(OperatingSys, Browser, Location, UserId, Token);

        return Ok(new
        {
            success = true
        });
    }

    [EnableRateLimiting("api")]
    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword ([FromBody] ChangePasswordDto request)
    {
        var CurrentPassword = request.CurrentPassword;
        var NewPassword = request.NewPassword;
        var UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(UserId)) return Unauthorized();

        if (NewPassword.Length < 8 || NewPassword.Length > 128)
        {
            return BadRequest("Invalid password length must be > 8 or < 128.");
        }

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT password_hash FROM users WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", UserId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return BadRequest("Username or password is invalid.");
        }

        var PasswordHash = reader.GetString(0);

        if (!datahandler.VerifyArgonHash(CurrentPassword, PasswordHash))
        {
            return BadRequest("Username or password is invalid.");
        }

        await reader.DisposeAsync();

        var NewPasswordHash = datahandler.ArgonHash(NewPassword);
        await using var UpdatePassword = new NpgsqlCommand(@"
            WITH deleted AS (
                DELETE FROM user_sessions
                WHERE user_id = @id
            )
            UPDATE users
            SET password_hash = @password_hash
            WHERE id = @id
            RETURNING 1;
        ", conn);
        UpdatePassword.Parameters.AddWithValue("id", UserId);
        UpdatePassword.Parameters.AddWithValue("password_hash", NewPasswordHash);

        var UpdateResult = await UpdatePassword.ExecuteScalarAsync();

        if (UpdateResult == null) 
        {
            return BadRequest("Username or password is invalid.");
        }

        return Ok(new
        {
            success = true
        });
    }
}