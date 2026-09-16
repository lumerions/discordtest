using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using System;
using System.Globalization;
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
using System.Text.RegularExpressions;
using Internal.ServerCont;
using Internal.Shared;
using OtpNet;

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
    [Required]
    [StringLength(128, MinimumLength = 128)]
    public required string NewPassword {get; init;}
}

public record SetUp2Fa 
{
    [Required]
    public required string Password {get; init;}
}

public record Enable2Fa 
{
    [Required]
    public required bool Enable {get; init;}
    public required string Code {get; init;}
}

public record Verify2Fa 
{
    [Required]
    public required string Code {get; init;}
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

public record DeleteSessionDto
{
    [Required]
    public required bool DeleteAllSessions {get; init;}
    public required int SessionId {get; init;}
}

[ApiController]
[Route("/api/internal/account/")]
public class AccountController : BaseController
{
    private readonly ServersController ServersContr;
    private readonly DataHandler datahandler;
    private readonly IConfiguration configuration;
    private readonly DatabaseHandler DBHandler;
    private readonly AuthenicationController Authenication;
    private readonly IHttpClientFactory HttpClientfactory;
    private readonly AccountHandler Accounts;
    public AccountController (ServersController ServersContr_, AccountHandler Accounts_, IHttpClientFactory HttpClientfactory_, DataHandler datahandler_, IConfiguration configuration_, DatabaseHandler DBHandler_, AuthenicationController Authenication_)
    {
        ServersContr = ServersContr_;
        datahandler = datahandler_;
        configuration = configuration_;
        DBHandler = DBHandler_;
        Authenication = Authenication_;
        HttpClientfactory = HttpClientfactory_;
        Accounts = Accounts_;
    }

    bool ValidUsername (string Username)
    {
        return Regex.IsMatch(Username, "^[a-zA-Z0-9_]{3,20}$");
    }

    public async Task<bool> SetSession (byte[] Emailciphertext, byte[] Emailnonce, byte[] Emailtag, byte[] EncryptKeyBytes, string Username, int UserId, bool? EmailIsProvided, string? EmailProvided)
    {
        try
        {
            string? EmailAddy = null;

            if (EmailIsProvided == true)
            {
                EmailAddy = EmailProvided;
            } else
            {
                EmailAddy = datahandler.Decrypt(Emailciphertext, Emailnonce, Emailtag, EncryptKeyBytes);
            }

            var Token = Authenication.SetJWTValue(configuration, UserId, EmailAddy, Username);
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
        } catch (Exception err)
        {
            return false;
        }
        return true;
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

        if (string.IsNullOrEmpty(UserIPAddress)) UserIPAddress = "Unknown";

        var UserAgent = HttpContext.Request.Headers["User-Agent"].ToString();
        var parser = Parser.GetDefault();
        var clientInfo = parser.Parse(UserAgent);
        var OS = clientInfo.OS.Family;
        var Browser = clientInfo.UA.Family;
        return (OS, Browser, UserIPAddress);
    }

    public string ValidateRequest (string Password, string Email, string? Username, bool? UsernameCheck)
    {
        var SecretKey = configuration["Main:HMacSha256Key"];
        var EncryptKey = configuration["Main:EncryptionKey"];

        if (string.IsNullOrEmpty(SecretKey))
        {
            return "Unexpected Error, please try again later.";
        }

        if (string.IsNullOrEmpty(EncryptKey))
        {
            return "Unexpected Error, please try again later.";
        }

        if (Password.Length < 8 || Password.Length > 128)
        {
            return "Invalid password length must be > 8 or < 128.";
        }

        if (Email.Length > 320) 
        {
            return "Email address is too long.";
        }

        if (UsernameCheck == true)
        {
            if (!ValidUsername(Username!))
            {
                return "Invalid username.";
            }
        }

        return "";
    }

    [EnableRateLimiting("api")]
    [HttpPost("login")]
    public async Task<IActionResult> Login ([FromBody] LoginDto request)
    {
        var Email = request.Email.Trim();
        var Password = request.Password;
        var SecretKey = configuration["Main:HMacSha256Key"];
        var EncryptKey = configuration["Main:EncryptionKey"];
        var ValidationResult = ValidateRequest(Password, Email, null, null);

        if (ValidationResult.Length > 0)
        {
            return BadRequest(ValidationResult);
        }

        byte[] SecretKeyBytes = Convert.FromBase64String(SecretKey!);
        var EmailHmacSha256 = datahandler.HmacSha256(Email, SecretKeyBytes);

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT password_hash, is_banned, id, username, ciphertext, tag, nonce, 2fa_enabled FROM users WHERE email_lookup = @email_lookup;",conn);
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

        if (!reader.IsDBNull(7))
        {
            bool MultiFactorAuthenicationEnabled = reader.GetBoolean(7);

            if (MultiFactorAuthenicationEnabled)
            {
                return Ok(new
                {
                    mfa = true,
                    success = true
                });
            }
        }

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        if (!datahandler.VerifyArgonHash(Password, PasswordHash))
        {
            return Unauthorized("Invalid username or password.");
        }

        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);

        await SetSession(ciphertext, nonce, tag, EncryptKeyBytes, Username, UserId, true, Email);

        return Ok(new
        {
            success = true
        });
    }

    [EnableRateLimiting("api")]
    [HttpPost("register")]
    public async Task<IActionResult> Register ([FromBody] RegisterDto request)
    {
        var Email = request.Email.Trim();
        var Password = request.Password;
        var Username = request.Username.Trim();
        var DayBorn = request.Day;
        var MonthBorn = request.Month;
        var YearBorn = request.Year;
        var SecretKey = configuration["Main:HMacSha256Key"];
        var EncryptKey = configuration["Main:EncryptionKey"];
        var ValidationResult = ValidateRequest(Password, Email, Username, true);
        var InvalidDobMessage = "Invalid dob, must be atleast 13 years old.";

        if (ValidationResult.Length > 0)
        {
            return BadRequest(ValidationResult);
        }

        if (!int.TryParse(DayBorn, out var DayBornInt) || !int.TryParse(MonthBorn, out var MonthBornInt) || !int.TryParse(YearBorn, out var YearBornInt))
        {
            return BadRequest(InvalidDobMessage);
        }

        if (DayBornInt <= 0 || MonthBornInt <= 0 || YearBornInt <= 0)
        {
            return BadRequest(InvalidDobMessage);
        }

        if (DayBornInt > 31 || MonthBornInt > 12 || YearBornInt > 2013)
        {
            return BadRequest(InvalidDobMessage);
        }

        var DOBString = $"{DayBornInt}/{MonthBornInt}/{YearBornInt}";

        if (!DateTime.TryParse(DOBString, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime TimeResult))
        {
            return BadRequest(InvalidDobMessage);
        }

        if (TimeResult > DateTime.Today.AddYears(-13))
        {
            return BadRequest(InvalidDobMessage);
        }

        byte[] SecretKeyBytes = Convert.FromBase64String(SecretKey!);
        var EmailHmacSha256 = datahandler.HmacSha256(Email, SecretKeyBytes);
        var PasswordHash = datahandler.ArgonHash(Password);
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
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

        await SetSession(ciphertext, nonce, tag, EncryptKeyBytes, Username, UserId, true, Email);

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword ([FromBody] ChangePasswordDto request)
    {
        var CurrentPassword = request.CurrentPassword;
        var NewPassword = request.NewPassword;
        int Id = 0;

        if (!ServersContr.GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT password_hash FROM users WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", Id);

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

        UpdatePassword.Parameters.AddWithValue("id", Id);
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

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("setup-2fa")]
    public async Task<IActionResult> SetUp2Fa ([FromBody] SetUp2Fa request)
    {
        var Password = request.Password;
        int Id = 0;

        if (!ServersContr.GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var EncryptKey = configuration["Main:EncryptionKey"];
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT is_banned, id, username, ciphertext, tag, nonce, password_hash FROM users WHERE id = @id;",conn);

        cmd.Parameters.AddWithValue("id", Id);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized("Invalid username or password.");
        }

        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
        {
            return BadRequest("2FA setup has not been started.");
        }

        var Banned = reader.GetInt32(0);
        var UserId = reader.GetInt32(1);
        var Username = reader.GetString(2);
        byte[] ciphertext = reader.GetFieldValue<byte[]>(3);
        byte[] tag = reader.GetFieldValue<byte[]>(4);
        byte[] nonce = reader.GetFieldValue<byte[]>(5);
        var CurrentPassword = reader.GetString(6);

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        if (!datahandler.VerifyArgonHash(Password, CurrentPassword))
        {
            return BadRequest("Username or password is invalid.");
        }

        var UserEmail = datahandler.Decrypt(ciphertext, nonce, tag, EncryptKeyBytes);

        if (UserEmail == null)
        {
            return BadRequest("Email not verified.");
        }

        if (string.IsNullOrEmpty(UserEmail))
        {
            return BadRequest("Email not verified.");
        }

        await reader.DisposeAsync();

        byte[] SecretBytes = RandomNumberGenerator.GetBytes(20);
        var Secret2FA = Base32Encoding.ToString(SecretBytes);
        var EncryptionResult = datahandler.Encrypt(Secret2FA, EncryptKeyBytes);
        var fa_nonce = EncryptionResult.nonce;
        var fa_ciphertext = EncryptionResult.ciphertext;
        var fa_tag = EncryptionResult.tag;

        await using var Update2FA = new NpgsqlCommand(@"
            UPDATE users
            SET
                2fa_ciphertext = @2fa_ciphertext,
                2fa_tag = @2fa_tag,
                2fa_nonce = @2fa_nonce
            WHERE id = @id
            AND 2fa_enabled = FALSE
            RETURNING id;
        ", conn);

        Update2FA.Parameters.AddWithValue("id", Id);
        Update2FA.Parameters.AddWithValue("2fa_ciphertext", fa_ciphertext);
        Update2FA.Parameters.AddWithValue("2fa_tag", fa_tag);
        Update2FA.Parameters.AddWithValue("2fa_nonce", fa_nonce);

        var UpdateResult = await Update2FA.ExecuteScalarAsync();

        if (UpdateResult == null) 
        {
            return BadRequest("Error enabling 2fa, please try again later.");
        }

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("enable-2fa")]
    public async Task<IActionResult> Enable2FA ([FromBody] Enable2Fa request)
    {
        var JwtAuthenicationToken = Request.Cookies["jwt"];
        var EnableAuthenicator = request.Enable;
        var AuthenicatorCode = request.Code;
        int Id = 0;

        if (!ServersContr.GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var EncryptKey = configuration["Main:EncryptionKey"];
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT is_banned, id, username, 2fa_ciphertext, 2fa_tag, 2fa_nonce FROM users WHERE id = @id;",conn);

        cmd.Parameters.AddWithValue("id", Id);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized("Invalid username or password.");
        }

        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
        {
            return BadRequest("2FA setup has not been started.");
        }

        var Banned = reader.GetInt32(0);
        var UserId = reader.GetInt32(1);
        var Username = reader.GetString(2);
        byte[] ciphertext = reader.GetFieldValue<byte[]>(3);
        byte[] tag = reader.GetFieldValue<byte[]>(4);
        byte[] nonce = reader.GetFieldValue<byte[]>(5);

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        var SecretKeyDecrypted = datahandler.Decrypt(ciphertext, nonce, tag, EncryptKeyBytes);

        if (string.IsNullOrEmpty(SecretKeyDecrypted))
        {
            return BadRequest("Unable to verify 2FA.");
        }

        if (!SharedMethods.Verify2FACode(AuthenicatorCode, SecretKeyDecrypted))
        {
            return Unauthorized("Invalid 2FA Code.");
        }

        await reader.DisposeAsync();

        if (string.IsNullOrEmpty(JwtAuthenicationToken))
        {
            return Unauthorized("Not logged in.");
        }

        await using var Update2FA = new NpgsqlCommand($"""
            WITH users_update AS (
                UPDATE users
                SET
                    2fa_enabled = @enable
                WHERE id = @id
                RETURNING id
            ),
            session_update AS (
                DELETE FROM user_sessions
                WHERE session_token <> @session_token
                RETURNING session_token
            )
            SELECT u.id
            FROM users_update u
            CROSS JOIN session_update s;
        """, conn);

        Update2FA.Parameters.AddWithValue("id", Id);
        Update2FA.Parameters.AddWithValue("2fa_enabled", EnableAuthenicator);
        Update2FA.Parameters.AddWithValue("session_token", JwtAuthenicationToken);

        var UpdateResult = await Update2FA.ExecuteScalarAsync();

        if (UpdateResult == null) 
        {
            return BadRequest("Error changing 2fa status, please try again later.");
        }

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("verify-2fa")]
    public async Task<IActionResult> Verify2FA ([FromBody] Verify2Fa request)
    {
        var JwtAuthenicationToken = Request.Cookies["jwt"];
        var AuthenicatorCode = request.Code;
        int Id = 0;

        if (!ServersContr.GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        var EncryptKey = configuration["Main:EncryptionKey"];
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT is_banned, id, username, 2fa_ciphertext, 2fa_tag, 2fa_nonce, ciphertext, tag, nonce FROM users WHERE id = @id;",conn);

        cmd.Parameters.AddWithValue("id", Id);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized("Invalid username or password.");
        }

        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
        {
            return BadRequest("2FA setup has not been started.");
        }

        var Banned = reader.GetInt32(0);
        var UserId = reader.GetInt32(1);
        var Username = reader.GetString(2);
        byte[] ciphertext = reader.GetFieldValue<byte[]>(3);
        byte[] tag = reader.GetFieldValue<byte[]>(4);
        byte[] nonce = reader.GetFieldValue<byte[]>(5);
        byte[] Emailciphertext = reader.GetFieldValue<byte[]>(6);
        byte[] Emailtag = reader.GetFieldValue<byte[]>(7);
        byte[] Emailnonce = reader.GetFieldValue<byte[]>(8);

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        var SecretKeyDecrypted = datahandler.Decrypt(ciphertext, nonce, tag, EncryptKeyBytes);

        if (string.IsNullOrEmpty(SecretKeyDecrypted))
        {
            return BadRequest("Unable to verify 2FA.");
        }

        if (!SharedMethods.Verify2FACode(AuthenicatorCode, SecretKeyDecrypted))
        {
            return Unauthorized("Invalid 2FA Code.");
        }

        await reader.DisposeAsync();

        if (string.IsNullOrEmpty(JwtAuthenicationToken))
        {
            return Unauthorized("Not logged in.");
        }

        await SetSession(Emailciphertext, Emailnonce, Emailtag, EncryptKeyBytes, Username, UserId, null, null);

        return Ok(new
        {
            success = true
        });
    }

    [Authorize]
    [EnableRateLimiting("api")]
    [HttpPost("delete-session")]
    public async Task<IActionResult> DeleteSession ([FromBody] DeleteSessionDto request)
    {
        var JwtAuthenicationToken = Request.Cookies["jwt"];
        var DeleteAll = request.DeleteAllSessions;
        var SessionId = request.SessionId;
        int Id = 0;

        if (!ServersContr.GetIdValue(ref Id))
        {
            return Unauthorized();
        }

        if (string.IsNullOrEmpty(JwtAuthenicationToken))
        {
            return Unauthorized("Not logged in.");
        }

        var EncryptKey = configuration["Main:EncryptionKey"];
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand("SELECT is_banned FROM users WHERE id = @id;",conn);

        cmd.Parameters.AddWithValue("id", Id);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized("Account not found.");
        }

        var Banned = reader.GetInt32(0);

        if (Banned == 1 || Banned == 2)
        {
            return Unauthorized();
        }

        await reader.DisposeAsync();

        await Accounts.DeleteUserSession(SessionId, Id, DeleteAll);

        return Ok(new
        {
            success = true
        });
    }
}