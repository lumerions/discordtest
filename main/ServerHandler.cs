using System;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Internal.Messages;
using Internal.Roles;
using Internal.Shared;
using Internal.Database;
using Internal.Redis;
using Npgsql;
using System.Text.Json;
using System.Data;

namespace Internal.Servers;

public record Members (
    Guid RoleId,
    int RoleHolderId,
    int RolePosition,
    string RoleName,
    int RoleColor,
    long Permissions,
    string RoleHolderName
);

public record Role (
    Guid RoleId,
    string RoleName,
    int Color,
    long Position,
    bool Separated
);

public record Message (
    Guid id,
    int sender_id,
    string message_content,
    DateTime created_at,
    bool edited,
    string Picture_Path
);

public class ChannelInformation
{
    public Guid ChannelId {get; set;}
    public string ChannelType {get; set;}
    public bool IsUnread {get; set;}
}

public class Server
{
    private readonly SharedMethods.WebSocketSessionManager Manager;
    private readonly RedisHandler RedisHandler_;
    private readonly DatabaseHandler DBHandler;
    private readonly MessageHandler MsgHandler;
    private readonly SharedMethods.ServerIdUserIdConnections ServerIdUserIdConns;
    private readonly static HashSet<string> AllowedColumns = new (StringComparer.OrdinalIgnoreCase) {
        "rules_channel",
        "channel_topic",
        "position",
        "type",
        "name",
    };
    
    public Server (RedisHandler RedisHandler__, SharedMethods.ServerIdUserIdConnections ServerIdUserIdConns_, DatabaseHandler handler_, MessageHandler MsgHandler_, SharedMethods.WebSocketSessionManager manager)
    {
        RedisHandler_ = RedisHandler__;
        DBHandler = handler_;
        MsgHandler = MsgHandler_;
        Manager = manager;
        ServerIdUserIdConns = ServerIdUserIdConns_;
    }

    public async Task<bool> DeleteGuild(Guid ServerId, int ServerOwnerId)
    {
        try
        {
            return await DBHandler.ExecuteAsync($"""
                DELETE FROM servers WHERE id = @id AND server_owner_id = @server_owner_id;
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", ServerId);
                cmd.Parameters.AddWithValue("server_owner_id", ServerOwnerId);
            }).ContinueWith(r => r.Result > 0);
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }

    public async Task<bool> CreateNewServer(string ServerName, int OwnerUserId, string OwnerName)
    {
        try
        {
            return await DBHandler.ExecuteAsync($"""
                WITH new_server AS (
                    INSERT INTO servers (server_owner_id, server_name)
                    SELECT
                        @server_owner_id,
                        @server_name
                    WHERE (
                        SELECT COUNT(*)
                        FROM servers
                        WHERE server_owner_id = @server_owner_id
                    ) < 101
                    RETURNING id
                ),
                new_member AS (
                    INSERT INTO server_members (server_id, user_id, nickname)
                    SELECT
                        id,
                        @server_owner_id,
                        @nickname
                    FROM new_server
                ),
                new_channels AS (
                    INSERT INTO server_channels (
                        server_id,
                        name,
                        type,
                        position,
                        rules_channel
                    )
                    SELECT id, 'general', 'text', 0, FALSE
                    FROM new_server

                    UNION ALL

                    SELECT id, 'rules', 'text', 1, TRUE
                    FROM new_server

                    RETURNING id, server_id, name
                ),
                new_server_setting AS (
                    INSERT INTO server_settings (server_id, systems_channel)
                    SELECT
                        server_id,
                        id
                    FROM new_channels
                    WHERE name = 'general'
                ),
                new_server_automod AS (
                    INSERT INTO server_automod (server_id)
                    SELECT
                        server_id,
                        id
                    FROM new_channels
                    WHERE name = 'general'
                )
                SELECT id FROM new_server;
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("server_owner_id", OwnerUserId);
                cmd.Parameters.AddWithValue("server_name", ServerName);
                cmd.Parameters.AddWithValue("nickname", OwnerName);
            }).ContinueWith(r => r.Result > 0);
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }
    public async Task<bool> CreateServerRole(string RoleName, int Color, bool Separated, int Position, long Permissions)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("INSERT INTO server_roles (name, color, position, separated, permissions) VALUES (@name, @color, @position, @separated, @permissions) RETURNING id;",conn);
            cmd.Parameters.AddWithValue("name", RoleName);
            cmd.Parameters.AddWithValue("color", Color);
            cmd.Parameters.AddWithValue("position", Position);
            cmd.Parameters.AddWithValue("separated", Separated);
            cmd.Parameters.AddWithValue("permissions", Permissions);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }

    public async Task<string> JoinServer(Guid ServerId, int JoinerId, string JoinerUsername, string InviteCode)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var IsBannedCommand = new NpgsqlCommand(@"
                SELECT reason
                FROM server_bans
                WHERE user_id = @user_id
                AND server_id = @server_id;

                SELECT is_revoked, id
                FROM server_invites
                WHERE id = @InviteCode
                AND (expires_at IS NULL OR expires_at > NOW())
                AND (max_uses = 32000 OR uses < max_uses);

                SELECT systems_channel
                FROM server_settings
                WHERE server_id = @server_id;
            ", conn);

            IsBannedCommand.Parameters.AddWithValue("user_id", JoinerId);
            IsBannedCommand.Parameters.AddWithValue("server_id", ServerId);
            IsBannedCommand.Parameters.AddWithValue("InviteCode", InviteCode);
            await using var reader = await IsBannedCommand.ExecuteReaderAsync();

            var InviteId = Guid.Empty;

            if (await reader.ReadAsync())
            {
                var banNote = reader.GetString(0);
                return $"You are banned from this server for {banNote}.";
            }

            if (await reader.NextResultAsync())
            {
                if (!await reader.ReadAsync())
                {
                    return "Invite is expired or invalid.";
                }

                var isRevoked = reader.GetBoolean(0);
                InviteId = reader.GetGuid(1);

                if (isRevoked )
                {
                    return "Invites are paused for this server";
                }
            }

            await using var transaction = await conn.BeginTransactionAsync();

            try {
                if (await reader.NextResultAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        await using var joinServerCommand = new NpgsqlCommand(@"
                            WITH server_members_write AS (
                                INSERT INTO server_members (
                                    server_id,
                                    user_id,
                                    nickname
                                )
                                VALUES (
                                    @server_id,
                                    @user_id,
                                    @nickname
                                )
                                RETURNING joined_at
                            ),
                            uses_update AS (
                                UPDATE server_invites
                                SET uses = uses + 1
                                WHERE id = @InviteId
                            )
                            SELECT server_members_write.joined_at
                            FROM server_members_write;
                        ", conn, transaction);

                        Func<string, string> WelcomeUser = userName => $"Welcome {userName} to the server!";
                        var SystemChannelId = reader.GetGuid(0);

                        joinServerCommand.Parameters.AddWithValue("user_id", JoinerId);
                        joinServerCommand.Parameters.AddWithValue("user_id", JoinerId);
                        joinServerCommand.Parameters.AddWithValue("server_id", ServerId);
                        joinServerCommand.Parameters.AddWithValue("nickname", JoinerUsername);
                        await reader.DisposeAsync();
                        var result = await joinServerCommand.ExecuteScalarAsync();
                        var success = result != null && result != DBNull.Value;
                        var returnMessage = success ? "Joined Server Successfully." : "Could not join server please try again later.";

                        if (success)
                        {
                            await MsgHandler.SendMessageInServer(WelcomeUser(JoinerUsername), JoinerId, SystemChannelId, "", true, transaction, "s");
                        }
                        
                        return returnMessage;
                    }
                }

                await transaction.CommitAsync();
            } catch
            {
                await transaction.RollbackAsync();
            }

            return "Could not join server please try again later.";

        } catch(Exception err) {
            Console.WriteLine(err);
            return "Internal Server Error.";
        }
    }

    public async Task<Dictionary<string, string>> GetChannelIdsByServerId(Guid ServerId, int? UserId, bool? PermissionsCheck)
    {
        var Data = new Dictionary<string, string>();

        try
        {
            await using var conn = await DBHandler.GetConnection();
            string SQL = PermissionsCheck == false || PermissionsCheck == null
                ? @"
                    SELECT
                        c.id,
                        c.type,
                        CASE
                            WHEN latest.id IS NULL THEN FALSE
                            WHEN cr.last_read_message_id IS NULL THEN TRUE
                            WHEN latest.created_at > read_msg.created_at THEN TRUE
                            WHEN latest.created_at = read_msg.created_at
                                AND latest.id <> read_msg.id THEN TRUE
                            ELSE FALSE
                        END AS is_unread
                    FROM server_channels c
                    LEFT JOIN channel_reads cr
                        ON cr.channel_id = c.id
                        AND cr.user_id = @user_id
                    LEFT JOIN server_messages read_msg
                        ON read_msg.id = cr.last_read_message_id
                    LEFT JOIN LATERAL (
                        SELECT id, created_at
                        FROM server_messages
                        WHERE channel_id = c.id
                        ORDER BY created_at DESC, id DESC
                        LIMIT 1
                    ) latest ON TRUE
                    WHERE c.server_id = @server_id;
                "
                : @"
                    SELECT bit_or(permissions) AS effective_permissions
                    FROM server_roles
                    WHERE user_id = @user_id
                    AND server_id = @server_id;

                    SELECT
                        c.id,
                        c.type,
                        CASE
                            WHEN latest.id IS NULL THEN FALSE
                            WHEN cr.last_read_message_id IS NULL THEN TRUE
                            WHEN latest.created_at > read_msg.created_at THEN TRUE
                            WHEN latest.created_at = read_msg.created_at
                                AND latest.id <> read_msg.id THEN TRUE
                            ELSE FALSE
                        END AS is_unread
                    FROM server_channels c
                    LEFT JOIN channel_reads cr
                        ON cr.channel_id = c.id
                        AND cr.user_id = @user_id
                    LEFT JOIN server_messages read_msg
                        ON read_msg.id = cr.last_read_message_id
                    LEFT JOIN LATERAL (
                        SELECT id, created_at
                        FROM server_messages
                        WHERE channel_id = c.id
                        ORDER BY created_at DESC, id DESC
                        LIMIT 1
                    ) latest ON TRUE
                    WHERE c.server_id = @server_id;
                ";

            await using var getChannelIds = new NpgsqlCommand(SQL, conn);

            if (PermissionsCheck == true)
            {
                getChannelIds.Parameters.AddWithValue("user_id", UserId!);
            }

            getChannelIds.Parameters.AddWithValue("server_id", ServerId);
            await using var reader = await getChannelIds.ExecuteReaderAsync();

            if (PermissionsCheck == true)
            {
                if (await reader.ReadAsync())
                {
                    var PermissionsNumber = reader.GetInt64(0);
                    Data.TryAdd("Permissions", PermissionsNumber.ToString());
                }

                await reader.NextResultAsync();
            }

            while (await reader.ReadAsync())
            {
                var DiscordChannelId = reader.GetGuid(0);
                var ChannelType = reader.GetString(1);
                var IsUnread = reader.GetBoolean(2);
                var IsForumChannel = ChannelType == "forum";
               // var RedisKey = $"channels:{DiscordChannelId.ToString()}";
              //  Data.TryAdd(RedisKey, "");
                var ChannelInfo = JsonSerializer.Serialize(new ChannelInformation
                {
                    ChannelId = DiscordChannelId,
                    ChannelType = ChannelType,
                    IsUnread = IsUnread
                });
                Data.TryAdd(DiscordChannelId.ToString(), ChannelInfo);
            }

            return Data;
        } catch (Exception error) {
            Console.WriteLine(error);
            return Data;
        }
    }

    public async Task<bool> BanOrMuteUser(Guid ServerId, int BanId, int ModeratorId, string BanReason, DateTime? ExpiresAt, string TableName)
    {
        if (TableName != "server_mutes" && TableName != "server_bans")
        {
            return false;
        }
        
        try
        {
            return await DBHandler.ExecuteAsync($"""
                INSERT INTO {TableName} (
                    server_id,
                    user_id,
                    moderator_id,
                    reason,
                    expires_at
                )
                VALUES (
                    @server_id,
                    @user_id,
                    @moderator_id,
                    @reason,
                    @expires_at
                );
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("user_id", BanId);
                cmd.Parameters.AddWithValue("moderator_id", ModeratorId);
                cmd.Parameters.AddWithValue("reason", BanReason);
                cmd.Parameters.AddWithValue("expires_at", (object?) ExpiresAt ?? DBNull.Value);
            }).ContinueWith(r => r.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> CreateNewServerInvite(Guid ServerId, int CreatorId, int MaxUses, Guid ChannelId, string ExpiresAt)
    {   
        var newInviteCode = RandomNumberGenerator.GetHexString(32);
        try
        {
            return await DBHandler.ExecuteAsync(@"
                INSERT INTO server_invites (
                    server_id,
                    created_by,
                    code,
                    channel_id,
                    max_uses,
                    expires_at
                )
                VALUES (
                    @server_id,
                    @created_by,
                    @code,
                    @channel_id,
                    @max_uses,
                    CASE @expiration
                        WHEN '30m'  THEN NOW() + INTERVAL '30 minutes'
                        WHEN '1h'   THEN NOW() + INTERVAL '1 hour'
                        WHEN '6h'   THEN NOW() + INTERVAL '6 hours'
                        WHEN '12h'  THEN NOW() + INTERVAL '12 hours'
                        WHEN '1d'   THEN NOW() + INTERVAL '1 day'
                        WHEN '7d'   THEN NOW() + INTERVAL '7 days'
                        WHEN '30d'  THEN NOW() + INTERVAL '30 days'
                        WHEN 'Never' THEN NULL
                    END
                );
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("created_by", CreatorId);
                cmd.Parameters.AddWithValue("code", newInviteCode);
                cmd.Parameters.AddWithValue("channel_id", ChannelId);
                cmd.Parameters.AddWithValue("max_uses", MaxUses);
                cmd.Parameters.AddWithValue("expiration", ExpiresAt);
            }).ContinueWith(v => v.Result > 0);

        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> KickUser(Guid ServerId, int UserId)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                DELETE FROM server_members 
                WHERE user_id = @user_id AND server_id = @server_id;
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("user_id", UserId);
            }).ContinueWith(v => v.Result > 0);

        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<int> GetUserIdByName(string Username)
    {
        try
        {
            var RedisDb = RedisHandler_.GetRedisDatabase();

            string? CachedId = await RedisDb.StringGetAsync(Username);

            if (int.TryParse(CachedId, out var CachedUserId))
            {
                return CachedUserId;
            }

            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"SELECT id FROM users WHERE username = @username;",conn);
            cmd.Parameters.AddWithValue("username", Username);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return 0;
            }

            var UserId = reader.GetInt32(0);

            await RedisDb.StringSetAsync(Username, UserId.ToString(), TimeSpan.FromDays(10));

            return UserId;
        } catch (Exception error) {
            Console.WriteLine(error);
            return 0;
        }
    }

    public async Task<bool> ChangeServerNickname(Guid ServerId, int UserId, string Nickname)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                UPDATE server_members
                SET nickname = @nickname
                WHERE user_id = @user_id AND server_id = @server_id;
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("user_id", UserId);
                cmd.Parameters.AddWithValue("nickname", Nickname);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> RevokeInvite(Guid ServerId, string InviteCode)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                UPDATE server_invites SET is_revoked = @is_revoked WHERE server_id = @ServerId AND code = @code;
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("ServerId", ServerId);
                cmd.Parameters.AddWithValue("code", InviteCode);
                cmd.Parameters.AddWithValue("is_revoked", true);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> CreateServerChannel(Guid ServerId, string ChannelType, int Position, string ChannelName, string ChannelTopic)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                INSERT INTO server_channels (
                    server_id,
                    name,
                    type,
                    position,
                    channel_topic
                )
                VALUES (
                    @server_id,
                    @name,
                    @type,
                    @position,
                    @channel_topic
                );
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("type", ChannelType);
                cmd.Parameters.AddWithValue("position", Position);
                cmd.Parameters.AddWithValue("name", ChannelName);
                cmd.Parameters.AddWithValue("channel_topic", ChannelTopic);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> ChangeChannelPosition(Guid ServerId, int Position, int NewPosition)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                UPDATE server_channels
                SET position = @new_position
                WHERE server_id = @server_id AND position = @position;
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("position", Position);
                cmd.Parameters.AddWithValue("new_position", NewPosition);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }


    public async Task<List<Members>> GetMemberList (Guid ServerId, Guid? LastId, int? LastPosition)
    {
        await using var conn = await DBHandler.GetConnection();
        string MemberGetSql = LastId == null ? @"
            SELECT 
                sr.id,
                sr.user_id,
                sr.position,
                sr.name,
                sr.color,
                sr.permissions,
                u.username
            FROM server_roles sr
            JOIN users u ON u.id = sr.user_id
            WHERE sr.server_id = @serverId
            ORDER BY sr.position DESC, sr.id DESC
            LIMIT 50;" : @"
            SELECT 
                sr.id,
                sr.user_id,
                sr.position,
                sr.name,
                sr.color,
                sr.permissions,
                u.username
            FROM server_roles sr
            JOIN users u ON u.id = sr.user_id
            WHERE sr.server_id = @serverId
            AND (
                sr.position < @lastPosition
                OR (sr.position = @lastPosition AND sr.id < @lastId)
            )
            ORDER BY sr.position DESC, sr.id DESC
            LIMIT 50;
        ";

        await using var cmd = new NpgsqlCommand(MemberGetSql, conn);
        cmd.Parameters.AddWithValue("serverId", ServerId);

        if (LastId != null)
        {
            cmd.Parameters.AddWithValue("lastPosition", LastPosition);
            cmd.Parameters.AddWithValue("lastId", LastId);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        var RoleList = new List<Members>();

        while (await reader.ReadAsync())
        {
            var RoleId = reader.GetGuid(0);
            var RoleHolderId = reader.GetInt32(1);
            var RolePosition = reader.GetInt32(2);
            var RoleName = reader.GetString(3);
            var RoleColor = reader.GetInt32(4);
            var Permissions = reader.GetInt64(5);
            var RoleHolderUsername = reader.GetString(6);

            RoleList.Add(new Members
            (
                RoleId,
                RoleHolderId,
                RolePosition,
                RoleName,
                RoleColor,
                Permissions,
                RoleHolderUsername
            ));
        }

        return RoleList;
    }

    public async Task<List<Role>> ViewRolesById(Guid ServerId, int ViewRoleId)
    {
        var Roles = new List<Role>();

        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"SELECT id, name, color, position, separated FROM server_roles WHERE server_id = @server_id",conn);
            cmd.Parameters.AddWithValue("server_id", ServerId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var RoleId = reader.GetGuid(0);
                var RoleName = reader.GetString(1);
                var Color = reader.GetInt32(2);
                var Position = reader.GetInt64(3);
                var Separated = reader.GetBoolean(4);
                Roles.Add(new Role
                (
                    RoleId,
                    RoleName,
                    Color,
                    Position,
                    Separated
                ));
            }

            var HighestPositionRoles = Roles.OrderByDescending(item => item.Position).ToList();

            return HighestPositionRoles;
        } catch (Exception error) {
            Console.WriteLine(error);
            return Roles;
        }
    }

    public int GetOnlineCountByServerId (Guid ServerId)
    {
        return ServerIdUserIdConns.ServerIdUsers.TryGetValue(ServerId.ToString(), out var Users) ? Users.Count : 0;
    }

    public async Task<Dictionary<string, string>> GetServerInfoByInvite (Guid InviteCode)
    {
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            SELECT 
                SE.created_at,
                SI.server_id,
                SI.is_revoked,
                COUNT(SM.id) AS member_count
            FROM server_invites SI
            JOIN servers SE 
                ON SE.server_id = SI.server_id
            LEFT JOIN server_members SM 
                ON SM.server_id = SI.server_id
            WHERE SI.code = @code
            AND (SI.expires_at IS NULL OR SI.expires_at > NOW())
            AND (SI.max_uses = 32000 OR SI.uses < SI.max_uses)
            GROUP BY SE.created_at, SI.server_id, SI.is_revoked;
        ", conn);

        cmd.Parameters.AddWithValue("code", InviteCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        var ServerInfo = new Dictionary<string, string>();

        if (!await reader.ReadAsync())
        {
            return ServerInfo;
        }

        var ServerCreatedAt = reader.GetDateTime(0);
        var ServerId = reader.GetGuid(1);
        var IsRevoked = reader.GetBoolean(2);
        var MemberCount = reader.GetInt32(3);
        var OnlineMemberCount = GetOnlineCountByServerId(ServerId);

        ServerInfo.Add("OnlineMemberCount", OnlineMemberCount.ToString());
        ServerInfo.Add("MemberCount", MemberCount.ToString());
        ServerInfo.Add("IsRevoked", IsRevoked.ToString());

        return ServerInfo;
    }

    public async Task<long> GetPermissionNumber (Guid ServerId, int UserId)
    {
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            SELECT bit_or(permissions) AS effective_permissions
            FROM server_roles
            WHERE user_id = @user_id
            AND server_id = @server_id;
        ", conn);

        cmd.Parameters.AddWithValue("server_id", ServerId);
        cmd.Parameters.AddWithValue("user_id", UserId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return 0;
        }

        var PermissionsNumber = reader.GetInt64(0);

        return PermissionsNumber;
    }

    public async Task<bool> ChangeChannelIdWebhook (Guid ChannelId, Guid ServerId, int ChangerUserId, Guid WebhookId)
    {
        var PermissionsNumber = await GetPermissionNumber(ServerId, ChangerUserId);
        var Perm = (Permissions) PermissionsNumber;
        var CanMakeWebhooks = (Perm & Permissions.Administrator) != 0;

        if (!CanMakeWebhooks)
        {
            return false;
        }

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE server_channels_webhooks SET channel_id = @channel_id WHERE id = @id RETURNING id;
        ", conn);

        cmd.Parameters.AddWithValue("id", WebhookId);
        cmd.Parameters.AddWithValue("channel_id", ChannelId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return false;
        }

        return true;
    }

    public async Task<bool> AddChannelWebhook (Guid ChannelId, Guid ServerId, int ChangerUserId)
    {
        var PermissionsNumber = await GetPermissionNumber(ServerId, ChangerUserId);
        var Perm = (Permissions) PermissionsNumber;
        var CanMakeWebhooks = (Perm & Permissions.Administrator) != 0;

        if (!CanMakeWebhooks)
        {
            return false;
        }

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO server_channels_webhooks (creator_id, channel_id) 
            VALUES (@ChangerUserId, @ChannelId)
            RETURNING id;
        ", conn);

        cmd.Parameters.AddWithValue("ChannelId", ChannelId);
        cmd.Parameters.AddWithValue("ChangerUserId", ChangerUserId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return false;
        }

        return true;
    }

    public async Task<bool> SendChannelWebhookMessage (Guid WebhookId, string WebhookMessage)
    {
        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(@"
            SELECT 
                SCW.channel_id,
                SC.server_id
            FROM server_channels_webhooks AS SCW
            JOIN server_channels AS SC
                ON SCW.channel_id = SC.id
            WHERE SCW.id = @id;
        ", conn);

        cmd.Parameters.AddWithValue("id", WebhookId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return false;
        }

        var ChannelId = reader.GetGuid(0);
        var ServerId = reader.GetGuid(1);

        await MsgHandler.SendMessageInServer(WebhookMessage, 5, ChannelId, "", true, null, "s");

        return true;
    }

    public async Task<bool> ReactToMessage (bool PrivateMessage, Guid ServerId, Guid MessageId, Guid ReactionId, int ReacterId)
    {
        string TableName = "";

        if (PrivateMessage) {
            TableName = "private_message_reactions";
        } else
        {
            TableName = "server_message_reactions";
            var PermissionsNumber = await GetPermissionNumber(ServerId, ReacterId);
            var Perm = (Permissions) PermissionsNumber;
            var CanReact = (Perm & Permissions.AddReactions) != 0;

            if (!CanReact)
            {
                return false;
            }
        }

        await using var conn = await DBHandler.GetConnection();

        try
        {
            return await DBHandler.ExecuteAsync($"""
                INSERT INTO {TableName} (
                    message_id,
                    reaction_id,
                    user_id
                )
                VALUES (
                    @message_id,
                    @reaction_id,
                    @user_id
                )
                """, cmd =>
                {
                    cmd.Parameters.AddWithValue("message_id", MessageId);
                    cmd.Parameters.AddWithValue("reaction_id", ReactionId);
                    cmd.Parameters.AddWithValue("user_id", ReacterId);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> DeleteMessage (bool PrivateMessage, int DeleterId, Guid MessageId)
    {
        string TableName = "";

        if (PrivateMessage) {
            TableName = "private_messages";
        } else
        {
            TableName = "server_messages";
        }

        await using var conn = await DBHandler.GetConnection();

        try
        {
            await using var cmd = new NpgsqlCommand($"""
                SELECT 
                    SCW.channel_id,
                    SC.server_id,
                    SCW.sender_id,
                    COALESCE((
                        SELECT bit_or(SR.permissions)
                        FROM server_roles AS SR
                        WHERE SR.user_id = @user_id
                        AND SR.server_id = SC.server_id
                    ), 0) AS effective_permissions
                FROM {TableName} AS SCW
                JOIN server_channels AS SC
                    ON SCW.channel_id = SC.id
                WHERE SCW.id = @id;
            """, conn);

            cmd.Parameters.AddWithValue("id", MessageId);

            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return false;
            }

            var ChannelId = reader.GetGuid(0);
            var ServerId = reader.GetGuid(1);
            var SenderId = reader.GetInt32(2);
            var PermissionsNumber = reader.GetInt64(3);

            await reader.DisposeAsync();

            if (!PrivateMessage)
            {
                if (SenderId != DeleterId)
                {
                    var Perm = (Permissions) PermissionsNumber;
                    var CanDeleteMessages = (Perm & Permissions.ManageMessages) != 0;

                    if (!CanDeleteMessages)
                    {
                        return false;
                    }
                }
            }

            return await DBHandler.ExecuteAsync($"""
                DELETE FROM {TableName} WHERE id = @id;
                """, cmd =>
                {
                    cmd.Parameters.AddWithValue("id", MessageId);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> AutoModCustomWordChanges (Guid ServerId, int ChangerId, bool block_custom_words, string custom_words_list, string custom_phrases_words_allowed, int automod_word_violation_response, string automod_custom_words_rule_name, List<string> automod_channels_role_ids_bypass)
    {
        var AutomodChannelRoleIdsBypass = automod_channels_role_ids_bypass.ToArray();
        var AutomodChannelRoleIdsBypassJson = JsonSerializer.Serialize(AutomodChannelRoleIdsBypass);

        try
        {
            var PermissionsNumber = await GetPermissionNumber(ServerId, ChangerId);
            var Perm = (Permissions) PermissionsNumber;
            var CanManageServer = (Perm & Permissions.ManageServer) != 0;
            var Administrator = (Perm & Permissions.Administrator) != 0;

            if (!Administrator && !CanManageServer)
            {
                return false;
            }
        
            return await DBHandler.ExecuteAsync($"""
                UPDATE server_automod
                SET 
                    automod_custom_words_rule_name = @automod_custom_words_rule_name,
                    automod_word_violation_response = @automod_word_violation_response,
                    block_custom_words = @block_custom_words,
                    custom_phrases_words_allowed = @custom_phrases_words_allowed,
                    custom_words_list = @custom_words_list,
                    automod_channels_role_ids_bypass = @bypass::jsonb
                WHERE server_id = @server_id

                """, cmd =>
                {
                    cmd.Parameters.AddWithValue("automod_custom_words_rule_name", automod_custom_words_rule_name);
                    cmd.Parameters.AddWithValue("automod_word_violation_response", automod_word_violation_response);
                    cmd.Parameters.AddWithValue("custom_words_list", custom_words_list);
                    cmd.Parameters.AddWithValue("custom_phrases_words_allowed", custom_phrases_words_allowed);
                    cmd.Parameters.AddWithValue("block_custom_words", block_custom_words);
                    cmd.Parameters.AddWithValue("server_id", ServerId);
                    cmd.Parameters.AddWithValue("bypass", AutomodChannelRoleIdsBypassJson);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> UpdateChannelInfo (string Column, string Value, Guid ServerId)
    {
        object DBValue = null;

        if (!AllowedColumns.Contains(Column))
        {
            return false;
        }

        if (int.TryParse(Value, out var IntDBValue))
        {
            DBValue = IntDBValue;
        }

        if (bool.TryParse(Value, out var BoolDBValue))
        {
            DBValue = BoolDBValue;
        }

        await using var conn = await DBHandler.GetConnection();

        try
        {
            return await DBHandler.ExecuteAsync($"""
                UPDATE server_channels
                SET {Column} = @value 
                WHERE server_id = @server_id;
                """, cmd =>
                {
                    if (DBValue != null)
                    {
                        cmd.Parameters.AddWithValue("value", DBValue);
                    } else {
                        cmd.Parameters.AddWithValue("value", Value);
                    } 
                    
                    cmd.Parameters.AddWithValue("server_id", ServerId);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> PinMessage (Guid ServerId, Guid MessageId, int ChangerId, bool PrivateMessage)
    {
        try
        {
            var TableName = "";
            var UpdateSql = "";

            if (PrivateMessage) {
                var PermissionsNumber = await GetPermissionNumber(ServerId, ChangerId);
                var Perm = (Permissions) PermissionsNumber;
                var CanPinMessages = (Perm & Permissions.PinnedMessages) != 0;

                if (!CanPinMessages)
                {
                    return false;
                }
                
                TableName = "pm_pins";
                UpdateSql = $"""
                    INSERT INTO {TableName} (message_id, server_id)
                    VALUES (@message_id, @server_id);

                    DELETE FROM {TableName}
                    WHERE id = (
                        SELECT id FROM {TableName} 
                        WHERE (SELECT COUNT(*) FROM {TableName}) > 50
                        ORDER BY id ASC
                        LIMIT 1
                    );
                """;

            } else
            {
                TableName = "server_pins";

                UpdateSql = $"""
                    INSERT INTO {TableName} (message_id)
                    VALUES (@message_id);

                    DELETE FROM {TableName}
                    WHERE id = (
                        SELECT id FROM {TableName} 
                        WHERE (SELECT COUNT(*) FROM {TableName}) > 50
                        ORDER BY id ASC
                        LIMIT 1
                    );
                """;
            }

            return await DBHandler.ExecuteAsync(UpdateSql, cmd =>
                {
                    if (PrivateMessage)
                    {
                        cmd.Parameters.AddWithValue("server_id", ServerId);
                    }
                    cmd.Parameters.AddWithValue("message_id", MessageId);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<List<Guid>> ReadPinMessageHistory (Guid ServerId, bool PrivateMessage)
    {
        var Ids = new List<Guid>();

        try
        {
            var TableName = "";
            var ReadSql = "";

            if (PrivateMessage) {
                TableName = "pm_pins";
                ReadSql = $"SELECT * FROM {TableName} WHERE message_id = @message_id;"; // this wasnt fully finished
            } else
            {
                TableName = "server_pins";
                ReadSql = $"SELECT message_id FROM {TableName} WHERE server_id = @server_id;";
            }

            await using var conn = await DBHandler.GetConnection();

            await using var cmd = new NpgsqlCommand(ReadSql, conn);

            cmd.Parameters.AddWithValue("server_id", ServerId);

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var MessageId = reader.GetGuid(0);
                Ids.Add(MessageId);
            }

            return Ids;
        } catch (Exception error) {
            Console.WriteLine(error);
            return Ids;
        }
    }

    public async Task<Dictionary<string, List<string>>> GetServerInformation (Guid ServerId)
    {
        var ServerInfo = new Dictionary<string, List<string>>();

        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"
                SELECT
                    (SELECT COUNT(*)
                    FROM server_members
                    WHERE server_id = @ServerId) AS member_count,
                    (SELECT array_agg(channel_name)
                    FROM server_channels
                    WHERE server_id = @ServerId) AS channel_names,
                    (SELECT COUNT(*)
                    FROM server_boosts
                    WHERE server_id = @ServerId) AS server_boost_count;
            ", conn);

            cmd.Parameters.AddWithValue("ServerId", ServerId);

            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return ServerInfo;
            }

            var ServerMemberCount = reader.GetInt32(0);
            var OnlineMemberCount = GetOnlineCountByServerId(ServerId);
            var ServerChannels = reader.IsDBNull(1) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(1);
            var ServerBoostCount = reader.GetInt32(2);
            var MemberCountList = new List<string>();
            var OnlineMemberList = new List<string>();
            var ServerChannelsList = new List<string>();
            var ServerBoostList = new List<string>();

            MemberCountList.Add(ServerMemberCount.ToString());
            OnlineMemberList.Add(OnlineMemberCount.ToString());
            ServerBoostList.Add(ServerBoostCount.ToString());

            foreach (var item in ServerChannels)
            {
                ServerChannelsList.Add(item);
            }

            ServerInfo.Add("MemberCount", MemberCountList);
            ServerInfo.Add("OnlineMemberCount", OnlineMemberList);
            ServerInfo.Add("ServerChannels", ServerChannelsList);
            ServerInfo.Add("ServerBoostCount", ServerBoostList);

            return ServerInfo;
        } catch (Exception error) {
            Console.WriteLine(error);
            return ServerInfo;
        }
    }

    public async Task<string> EnableServerTag (Guid ServerId, int ChangerId, int ServerTagImageId)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"
                SELECT 
                    (SELECT COUNT(*) 
                    FROM server_boosts 
                    WHERE server_id = @ServerId) AS server_boost_count,

                    (SELECT boosts_spent 
                    FROM servers 
                    WHERE id = @ServerId) AS boosts_spent;
            ", conn);

            cmd.Parameters.AddWithValue("ServerId", ServerId);

            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return "This server has no boosts.";
            }

            var BoostCount = reader.GetInt32(0);
            var BoostsAvailable = reader.GetInt16(1);
            var CanSpendBoost = BoostCount - BoostsAvailable >= 3;

            if (!CanSpendBoost)
            {
                return "Not enough boosts.";
            }

            await reader.DisposeAsync();

            await using var WriteCmd = new NpgsqlCommand(@"
                UPDATE servers 
                SET boosts_spent = boosts_spent + 3 
                WHERE id = @ServerId;

                INSERT INTO server_tag (server_id, server_tag_id) 
                VALUES (@ServerId, @ServerTagImageId);
            ", conn);

            WriteCmd.Parameters.AddWithValue("ServerId", ServerId);
            WriteCmd.Parameters.AddWithValue("ServerTagImageId", ServerTagImageId);

            await WriteCmd.ExecuteNonQueryAsync();

            return "Success";
        } catch (Exception error) {
            Console.WriteLine(error);
            return "Internal Server Error.";
        }
    }

    public async Task<List<Message>> GetChatMessages (Guid ChannelId, Guid ServerId, int ViewId, bool InitGet, bool IsPrivateMessage, DateTime? LastCursor, Guid? LastMessageId)
    {
        var Messages = new List<Message>();

        try
        {
            var TableName = "server_messages";
            if (IsPrivateMessage) TableName = "private_messages";

            if (!IsPrivateMessage)
            {
                var PermissionsNumber = await GetPermissionNumber(ServerId, ViewId);
                var Perm = (Permissions) PermissionsNumber;
                var CanViewMsgHistory = (Perm & Permissions.ReadMessageHistory) != 0;
                // this can be way more optimized but its fine for now 
                if (!CanViewMsgHistory)
                {
                    return Messages;
                }
            }

            var Sql = InitGet == true ? $"""
                SELECT id, sender_id, message_content, created_at, edited, picture_path
                FROM {TableName}
                WHERE channel_id = @ChannelId
                ORDER BY created_at DESC, id DESC
                LIMIT 50;

                INSERT INTO channel_reads (
                    user_id,
                    channel_id,
                    last_read_message_id,
                    last_read_at
                )
                SELECT
                    @UserId,
                    @ChannelId,
                    id,
                    NOW()
                FROM {TableName}
                WHERE channel_id = @ChannelId
                ORDER BY created_at DESC, id DESC
                LIMIT 1
                ON CONFLICT (user_id, channel_id)
                DO UPDATE SET
                    last_read_message_id = EXCLUDED.last_read_message_id,
                    last_read_at = NOW();
                """ : $"""
                SELECT *
                FROM {TableName}
                WHERE channel_id = @ChannelId
                AND (
                    created_at < @BeforeCreatedAt
                    OR (
                        created_at = @BeforeCreatedAt
                        AND id < @BeforeId
                    )
                )
                ORDER BY created_at DESC, id DESC
                LIMIT 50;
                """;

            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(Sql, conn);

            cmd.Parameters.AddWithValue("ChannelId", ChannelId);

            if (LastCursor != null)
            {
                cmd.Parameters.AddWithValue("BeforeId", LastMessageId!);
                cmd.Parameters.AddWithValue("BeforeCreatedAt", LastCursor);
            }

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var sender_id = reader.GetInt32(1);
                var message_content = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var created_at = reader.GetDateTime(3);
                var edited = reader.GetBoolean(4);
                var Picture_Path = reader.GetString(5);

                Messages.Add(new Message
                (
                    id,
                    sender_id,
                    message_content,
                    created_at,
                    edited,
                    Picture_Path
                ));
            }

            return Messages;
        } catch (Exception error) {
            Console.WriteLine(error);
            return Messages;
        }
    }

    public async Task<List<Message>> SearchMessagesByWord (string? Search, int ViewId, Guid ServerId, Guid ChannelId, bool IsPrivateMessage, DateTime? cursorCreatedAt, Guid? cursorId)
    {
        var Messages = new List<Message>();

        try
        {
            var TableName = "server_messages";
            if (IsPrivateMessage) TableName = "private_messages";
            if (Search == null) Search = "";

            if (!IsPrivateMessage)
            {
                var PermissionsNumber = await GetPermissionNumber(ServerId, ViewId);
                var Perm = (Permissions) PermissionsNumber;
                var CanViewMsgHistory = (Perm & Permissions.ReadMessageHistory) != 0;
                
                if (!CanViewMsgHistory)
                {
                    return Messages;
                }
            }

            string SQL = cursorCreatedAt is null && cursorId is null
                ? $"""
                    SELECT id, sender_id, message_content, created_at, edited, picture_path
                    FROM {TableName}
                    WHERE channel_id = @channel_id
                    AND message_content LIKE CONCAT('%', @search, '%')
                    ORDER BY created_at DESC, id DESC
                    LIMIT 50;
                    """
                : $"""
                    SELECT id, sender_id, message_content, created_at, edited, picture_path
                    FROM {TableName}
                    WHERE channel_id = @channel_id
                    AND message_content LIKE CONCAT('%', @search, '%')
                    AND (created_at, id) < (@created_at, @id)
                    ORDER BY created_at DESC, id DESC
                    LIMIT 50;
                    """;

            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(SQL,conn);
            if (cursorCreatedAt != null && cursorId != null)
            {
                cmd.Parameters.AddWithValue("created_at", cursorCreatedAt);
                cmd.Parameters.AddWithValue("id", cursorId);
            }

            cmd.Parameters.AddWithValue("channel_id", ChannelId);
            cmd.Parameters.AddWithValue("search", Search);

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var sender_id = reader.GetInt32(1);
                var message_content = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var created_at = reader.GetDateTime(3);
                var edited = reader.GetBoolean(4);
                var Picture_Path = reader.GetString(5);

                Messages.Add(new Message
                (
                    id,
                    sender_id,
                    message_content,
                    created_at,
                    edited,
                    Picture_Path
                ));
            }

            return Messages;
        } catch (Exception error) {
            Console.WriteLine(error);
            return Messages;
        }
    }

    public async Task<string> CreateNewForumPost (string ForumTitle, Guid ChannelId, int UserId, Guid ServerId)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO server_forum_data (server_id, user_id, channel_id, forum_title)
                VALUES (@server_id, @user_id, @channel_id, @forum_title)
                RETURNING id;
            """, conn);

            cmd.Parameters.AddWithValue("server_id", ServerId);
            cmd.Parameters.AddWithValue("user_id", UserId);
            cmd.Parameters.AddWithValue("channel_id", ChannelId);
            cmd.Parameters.AddWithValue("forum_title", ForumTitle);

            var Result = await cmd.ExecuteScalarAsync();

            if (Result == null)
            {
                return "Failed to create new forum post, please try again later.";
            }

            return "Success";
        } catch (Exception error) {
            Console.WriteLine(error);
            return "Internal Server Error.";
        }
    }
}