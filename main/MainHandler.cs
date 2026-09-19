using System;
using System.Threading.Tasks;
using Internal.Database;
using Internal.Shared;
using Internal.Data;
using Internal.ServerCont;
using Internal.Messages;
using Npgsql;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using System.ComponentModel.Design;
using Microsoft.Extensions.Configuration;

namespace Internal.Main;

public class RoleItem
{
    public string RoleName {get; set;} = "";
    public string Color {get; set;}
    public int Position {get; set;}
    public bool Separated {get; set;}
    public string RoleIcon {get; set;} = "";
}

public class ProfileInfo
{
    public bool Banned {get; set;}
    public string Bio {get; set;} = "";
    public string ProfileName {get; set;} = "";
    public string AvatarImageUrl {get; set;} = "";
    public List<int> MutualFriendData {get; set;}
    public List<RoleItem> RoleData {get; set;}
    public List<Guid> MutualServerData {get; set;}
    public DateTime JoinDate {get; set;}
    public DateTime AccountCreated {get; set;}
    public int? MutualServers {get; set;}
    public Dictionary<string, string> ConnectionsData {get; set;}
}

public class Notification
{
    public int UserId {get; set;}
}

public class FriendRequest : Notification
{
    public string Username {get; set;}
}

public class MainHandler
{
    private static IConfiguration configuration;
    private readonly DataHandler datahandler;
    private readonly DatabaseHandler DBHandler;
    private readonly SharedMethods.WebSocketSessionManager Manager;
    private readonly ServersController ServerControll;
    private readonly MessageHandler MessageHand;
    public MainHandler(IConfiguration configuration_, MessageHandler MessageHand_, ServersController ServerController, DatabaseHandler databaseHandler, SharedMethods.WebSocketSessionManager manager, DataHandler datahandler_)
    {
        DBHandler = databaseHandler;
        Manager = manager;
        ServerControll = ServerController;
        MessageHand = MessageHand_;
        datahandler = datahandler_;
        configuration = configuration_;
    }

    public (bool IsOnline, WebSocket UserSocket) UserOnline (int UserId)
    {
        if (Manager.Users.TryGetValue(UserId.ToString(), out var Socket))
        {
           return (true, Socket);
        }

        return (true, null);
    }
    public async Task<ProfileInfo> GetProfileInfo(int UserId, int ViewerId, int? ServerId)
    {
        string SQL = ServerId == null
            ? @"SELECT username, about_me, is_banned, created_at
                FROM users
                WHERE id = @id;
                
                SELECT storage_path 
                FROM avatar_uploads
                WHERE user_id = @id
                ORDER BY created_at DESC
                LIMIT 1;
                
                SELECT url, connection_type
                FROM connections
                WHERE user_id = @id AND visible = TRUE;

                SELECT sm1.server_id
                FROM server_members sm1
                JOIN server_members sm2
                    ON sm1.server_id = sm2.server_id
                WHERE sm1.user_id = @id
                AND sm2.user_id = @id2;

                SELECT
                    CASE
                        WHEN user_id = @UserId THEN friend_id
                        ELSE user_id
                    END AS friend_id
                FROM friends
                WHERE user_id = @UserId OR friend_id = @UserId;

                SELECT personal_note
                FROM personal_profile_note
                WHERE user_id = @id;
                "
            : @"SELECT username, about_me, is_banned, created_at
                FROM users
                WHERE id = @id;

                SELECT storage_path 
                FROM avatar_uploads
                WHERE user_id = @id
                ORDER BY created_at DESC
                LIMIT 1;

                SELECT url, connection_type
                FROM connections
                WHERE user_id = @id AND visible = TRUE;

                SELECT sm1.server_id
                FROM server_members sm1
                JOIN server_members sm2
                    ON sm1.server_id = sm2.server_id
                WHERE sm1.user_id = @id
                AND sm2.user_id = @id2;

                SELECT
                    CASE
                        WHEN user_id = @id THEN friend_id
                        ELSE user_id
                    END AS friend_id
                FROM friends
                WHERE user_id = @id OR friend_id = @id;

                SELECT
                    r.name,
                    r.color,
                    r.position,
                    r.separated,
                    i.storage_path AS role_icon_storage_path
                FROM server_roles r
                LEFT JOIN role_icon_uploads i
                    ON i.role_id = r.id
                WHERE r.user_id = @id
                AND r.server_id = @server_id;
                
                SELECT joined_at
                FROM server_members
                WHERE server_id = @server_id AND user_id = @id;

                SELECT personal_note
                FROM personal_profile_note
                WHERE user_id = @id;
                ";

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(SQL, conn);

        cmd.Parameters.AddWithValue("id", UserId);
        cmd.Parameters.AddWithValue("id2", ViewerId);

        if (ServerId != null)
        {
            cmd.Parameters.AddWithValue("server_id", ServerId);
        }

        await using var Reader = await cmd.ExecuteReaderAsync();
        var UserName = "Deleted Account";
        var AboutMe = "";
        var Banned = true;
        var AvatarImage = "";
        var MutualServerServerIds = new List<Guid>();
        var MutualFriendIds = new List<int>();
        var UserRoleData = new List<RoleItem>();
        var Joined = DateTime.UtcNow;
        var JoinedDiscordia = DateTime.UtcNow;
        int? MutualServers = null;
        int? MutualFriends = null;
        var Note = "";
        var Connections = new Dictionary<string, string>();

        if (await Reader.ReadAsync()) {
            UserName = Reader.GetString(0);
            AboutMe = Reader.IsDBNull(0) ? "" : Reader.GetString(0);
            Banned = Reader.GetBoolean(2);
            JoinedDiscordia = Reader.GetDateTime(3);

            if (await Reader.NextResultAsync())
            {
                if (await Reader.ReadAsync())
                {
                    AvatarImage = Reader.IsDBNull(0) ? "" : Reader.GetString(0);
                }

                if (await Reader.NextResultAsync())
                {
                    Connections.TryAdd(Reader.GetString(1), Reader.GetString(0));
                }

                if (await Reader.NextResultAsync())
                {
                    MutualServers = 0;
                    while (await Reader.ReadAsync())
                    {
                        var ReaderServerId = Reader.GetGuid(0);
                        MutualServers += 1;
                        MutualServerServerIds.Add(ReaderServerId);
                    }
                }

                if (await Reader.NextResultAsync())
                {
                    MutualFriends = 0;
                    while (await Reader.ReadAsync())
                    {
                        var ReaderFriendId = Reader.GetInt32(0);
                        MutualFriends += 1;
                        MutualFriendIds.Add(ReaderFriendId);
                    }
                }
            
                if (await Reader.NextResultAsync())
                {
                    while (await Reader.ReadAsync())
                    {
                        var name = Reader.GetString(0);
                        var color = Reader.GetString(1);
                        var position = Reader.GetInt32(2);
                        var separated = Reader.GetBoolean(3);
                        var RoleIconImage = Reader.IsDBNull(4) ? "" : Reader.GetString(4);
                        UserRoleData.Add(new RoleItem
                        {
                            RoleName = name,
                            Color = color,
                            Position = position,
                            Separated = separated,
                            RoleIcon = RoleIconImage
                        });
                    }
                }

                if (await Reader.NextResultAsync())
                {
                    if (await Reader.ReadAsync())
                    {
                        var JoinedAt = Reader.GetDateTime(0);
                        Joined = JoinedAt;
                    }
                }

                if (await Reader.NextResultAsync())
                {
                    if (await Reader.ReadAsync())
                    {
                        Note = Reader.GetString(0);
                    }
                }
            }
        }
        
        var ProfileInformation = new ProfileInfo
        {
            Banned = Banned,
            Bio = AboutMe,
            ProfileName = UserName,
            AvatarImageUrl = AvatarImage,
            RoleData = UserRoleData,
            JoinDate = Joined,
            AccountCreated = JoinedDiscordia,
            MutualServers = MutualServers,
            MutualServerData = MutualServerServerIds,
            MutualFriendData = MutualFriendIds,
            ConnectionsData = Connections
        };

        return ProfileInformation;
    }

    public async Task<bool> SendFriendRequest (int RecieverId, int SenderId, string SenderUsername)
    {
        var RequestSuccessful =  await DBHandler.ExecuteAsync(@"
            INSERT INTO notifications (
                user_id,
                sender_id,
                request_id,
                type
            )
            SELECT
                u.id,
                @sender_id,
                @request_id,
                @type
            FROM users u
            WHERE u.id = @request_id
            AND u.profile_status != 2;
        ", cmd =>
        {
            cmd.Parameters.AddWithValue("sender_id", SenderId);
            cmd.Parameters.AddWithValue("request_id", RecieverId);
            cmd.Parameters.AddWithValue("type", true);
        }) > 0;

        if (RequestSuccessful)
        {
            var Result = UserOnline(RecieverId);
            var Online = Result.IsOnline;
            var Socket = Result.UserSocket;

            var UserJson = JsonSerializer.Serialize(new FriendRequest
            {
                UserId = SenderId,
                Username = SenderUsername
            });

            if (Online == true)
            {
                await ServerControll.SendUpdate(Socket, RecieverId.ToString(), UserJson);
            }
            return true;
        }

        return false;
    }

    public async Task<bool> ChangeProfileStatus (int ProfileStatusNumber, int ChangerId)
    {
        if (ProfileStatusNumber != 1 && ProfileStatusNumber != 2 && ProfileStatusNumber != 3)
        {
            return false;
        }
        
        var RequestSuccessful = await DBHandler.ExecuteAsync(@"
            UPDATE users
            SET profile_status = @ProfileStatusNumber 
            WHERE id = @ChangerId;
        ", cmd =>
        {
            cmd.Parameters.AddWithValue("ChangerId", ChangerId);
            cmd.Parameters.AddWithValue("ProfileStatusNumber", ProfileStatusNumber);
        }) > 0;

        return RequestSuccessful;
    }

    public async Task<bool> ApplyNitroSub (int UserId)
    {
        var RequestSuccessful = await DBHandler.ExecuteAsync(@"
            UPDATE users
            SET premium_expires_at = NOW() + INTERVAL '7 days'
            WHERE id = @UserId;
        ", cmd =>
        {
            cmd.Parameters.AddWithValue("UserId", UserId);
        }) > 0;

        return RequestSuccessful;
    }

    public async Task<bool> BoostServer (Guid ServerId, Guid ChannelId, int BoosterId, string BoosterName)
    {   

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO server_boosts (server_id, user_id)
            SELECT @ServerId, @UserId
            WHERE (
                SELECT COUNT(*)
                FROM server_boosts
                WHERE user_id = @UserId
            ) < 2
            AND EXISTS (
                SELECT 1
                FROM users u
                WHERE u.id = @UserId
                AND u.premium_expires_at IS NOT NULL
                AND NOW() < u.premium_expires_at
            )

            SELECT systems_channel
            FROM server_settings
            WHERE server_id = @ServerId;
        ", conn);

        cmd.Parameters.AddWithValue("ServerId", ServerId);
        cmd.Parameters.AddWithValue("UserId", BoosterId);
        await using var Reader = await cmd.ExecuteReaderAsync();

        if (await Reader.NextResultAsync())
        {
            if (await Reader.ReadAsync())
            {
                var SystemChannelId = Reader.GetGuid(0);
                await MessageHand.SendMessageInServer($"{BoosterName} just boosted the server!", BoosterId, ChannelId, "", true, null, "s");
            }
        }

        return true;
    }

    public async Task<bool> UpdateConnections (int UserId, string? ConnectionName, string? AccessToken, string? RefreshToken, string? Url, bool? Visible, string? ConnectionType)
    {
        if (ConnectionName == null) ConnectionName = "not set";
        if (RefreshToken == null) RefreshToken = "";
        if (AccessToken == null) AccessToken = "";
        if (Url == null) Url = "";
        if (ConnectionType == null) ConnectionType = "youtube";

        await using var conn = await DBHandler.GetConnection();

        var EncryptKey = configuration["Main:EncryptionKey"];
        var EncryptKeyBytes = Convert.FromBase64String(EncryptKey!);
        var EncryptionResultAccess = datahandler.Encrypt(AccessToken, EncryptKeyBytes);
        var nonceAccess = EncryptionResultAccess.nonce;
        var ciphertextAccess = EncryptionResultAccess.ciphertext;
        var tagAccess = EncryptionResultAccess.tag;
        var EncryptionResultRefresh = datahandler.Encrypt(RefreshToken, EncryptKeyBytes);
        var nonceRefresh = EncryptionResultRefresh.nonce;
        var ciphertextRefresh = EncryptionResultRefresh.ciphertext;
        var tagRefresh = EncryptionResultRefresh.tag;

        var UpdateSQL = ConnectionName == "no" && AccessToken == "no" && RefreshToken == "no" && Url == "no" ? """
            UPDATE connections
            SET visible = {Visible}
            WHERE connection_type = @connection_type AND user_id = @user_id;
        """ : """ 
            INSERT INTO connections
                (user_id, name, url, connection_type, is_active, refresh_token_ciphertext, refresh_token_tag, refresh_token_nonce, access_token_ciphertext, access_token_tag, access_token_nonce)
            VALUES
                (@user_id, @name, @url, @connection_type, @is_active, @refresh_token_ciphertext, @refresh_token_tag, @refresh_token_nonce, @access_token_ciphertext, @access_token_tag, @access_token_nonce)
        """;

        if (ConnectionName == "no" && AccessToken == "no" && RefreshToken == "no" && Url == "no")
        {
            await using var cmd = new NpgsqlCommand(UpdateSQL, conn);

            cmd.Parameters.AddWithValue("user_id", UserId);
            cmd.Parameters.AddWithValue("connection_type", ConnectionType);

            await cmd.ExecuteNonQueryAsync();
        } else
        {
            await using var cmd = new NpgsqlCommand(UpdateSQL, conn);

            cmd.Parameters.AddWithValue("user_id", UserId);
            cmd.Parameters.AddWithValue("name", ConnectionName);
            cmd.Parameters.AddWithValue("url", Url);
            cmd.Parameters.AddWithValue("connection_type", ConnectionType);
            cmd.Parameters.AddWithValue("refresh_token_ciphertext", ciphertextRefresh);
            cmd.Parameters.AddWithValue("refresh_token_tag", tagRefresh);
            cmd.Parameters.AddWithValue("refresh_token_nonce", nonceRefresh);
            cmd.Parameters.AddWithValue("access_token_ciphertext", ciphertextAccess);
            cmd.Parameters.AddWithValue("access_token_tag", tagAccess);
            cmd.Parameters.AddWithValue("access_token_nonce", nonceAccess);
            cmd.Parameters.AddWithValue("is_active", false);

            await cmd.ExecuteNonQueryAsync();
        }

        return true;
    }

    public async Task<string> GetYoutubeOauthLink (string ClientId, string Domain, string Code)
    {
        await using var conn = await DBHandler.GetConnection();

        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = Domain,
            ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/youtube.upload",
            ["access_type"] = "offline",
            ["state"] = Code
        };
        
        return QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", query);
    }

    public async Task<bool> StateValid (string StateCode, int UserId, string? ConnectionType)
    {
        if (ConnectionType == null) ConnectionType = "youtube";

        await using var conn = await DBHandler.GetConnection();

        await using var cmd = new NpgsqlCommand($"""
        SELECT statee FROM connections
        WHERE connection_type = @connection_type AND user_id = @user_id;
        """, conn);

        cmd.Parameters.AddWithValue("user_id", UserId);
        cmd.Parameters.AddWithValue("connection_type", ConnectionType);

        await using var reader = await cmd.ExecuteReaderAsync();

        var State = reader.GetString(0);

        if (StateCode != State) {
            return false;
        }

        return true;
    }

    public async Task<bool> ChangeProfileData (bool ChangeBio, int UserId, string Text)
    {   
        string ColumnName = "pronouns";

        if (ChangeBio) ColumnName = "bio";

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE users
            SET {ColumnName} = @Text
            WHERE id = @UserId;
        """, conn);

        cmd.Parameters.AddWithValue("Text", Text);
        cmd.Parameters.AddWithValue("UserId", UserId);

        await cmd.ExecuteNonQueryAsync();

        return true;
    }

    public async Task<bool> BanUser (int UserId, int BanNumber)
    {   
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE users
            SET is_banned = @BanNumber
            WHERE id = @UserId;
        """, conn);

        cmd.Parameters.AddWithValue("BanNumber", BanNumber);
        cmd.Parameters.AddWithValue("UserId", UserId);

        await cmd.ExecuteNonQueryAsync();

        return true;
    }

    public async Task<string> TurnOnServerTag (Guid ServerId, int ChangerId, int ServerTagId)
    {   
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand($"""
                UPDATE users
                SET server_tag_id = @ServerTagId
                WHERE id = @UserId
                AND EXISTS (
                    SELECT 1
                    FROM server_members SM
                    WHERE SM.user_id = @UserId AND SM.server_id = @ServerId
                )
                
                RETURNING id;
            """, conn);

            cmd.Parameters.AddWithValue("ServerTagId", ServerTagId);
            cmd.Parameters.AddWithValue("UserId", ChangerId);
            cmd.Parameters.AddWithValue("ServerId", ServerId);

            var Success = await cmd.ExecuteScalarAsync();

            if (Success != null)
            {
                return "Success";
            }

            return "You are no longer in this server.";
        } catch (Exception error)
        {
            return "Internal Server Error.";
        }
    }
}