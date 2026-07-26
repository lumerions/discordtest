using System;
using System.Linq;
using System.Security.Cryptography;
using Internal.Messages;
using Internal.Roles;
using Internal.Shared;
using Internal.Database;
using Npgsql;

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
    string? message_content,
    DateTime created_at,
    bool edited
);

public class Server
{
    private readonly SharedMethods.WebSocketSessionManager Manager;

    private readonly DatabaseHandler DBHandler;
    private readonly MessageHandler MsgHandler;
    private readonly SharedMethods.ServerIdUserIdConnections ServerIdUserIdConns;
    public Server (SharedMethods.ServerIdUserIdConnections ServerIdUserIdConns_, DatabaseHandler handler_, MessageHandler MsgHandler_, SharedMethods.WebSocketSessionManager manager)
    {
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
            cmd.Parameters.AddWithValue("seperated", Separated);
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

                SELECT is_revoked
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
                            RETURNING joined_at;
                        ", conn, transaction);

                        Func<string, string> WelcomeUser = userName => $"Welcome {userName} to the server!";
                        var SystemChannelId = reader.GetGuid(0);

                        joinServerCommand.Parameters.AddWithValue("user_id", JoinerId);
                        joinServerCommand.Parameters.AddWithValue("server_id", ServerId);
                        joinServerCommand.Parameters.AddWithValue("nickname", JoinerUsername);
                        var result = await joinServerCommand.ExecuteScalarAsync();
                        var success = result != null && result != DBNull.Value;
                        var returnMessage = success ? "Joined Server Successfully." : "Could not join server please try again later.";

                        if (success)
                        {
                            await MsgHandler.SendMessageInServer(WelcomeUser(JoinerUsername), JoinerId, SystemChannelId, "", true, transaction);
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
        try
        {
            await using var conn = await DBHandler.GetConnection();
            string SQL = PermissionsCheck == false || PermissionsCheck == null
                ? @"
                    SELECT id
                    FROM server_channels
                    WHERE server_id = @server_id;
                "
                : @"
                    SELECT bit_or(permissions) AS effective_permissions
                    FROM server_roles
                    WHERE user_id = @user_id
                    AND server_id = @server_id;

                    SELECT id
                    FROM server_channels
                    WHERE server_id = @server_id;
                ";
            await using var getChannelIds = new NpgsqlCommand(SQL, conn);

            if (PermissionsCheck == true)
            {
                getChannelIds.Parameters.AddWithValue("user_id", UserId!);
            }

            getChannelIds.Parameters.AddWithValue("server_id", ServerId);
            await using var reader = await getChannelIds.ExecuteReaderAsync();
            var Data = new Dictionary<string, string>();

            if (PermissionsCheck == true)
            {
                if (await reader.ReadAsync())
                {
                    var PermissionsNumber = reader.GetInt64(0);
                    Data.TryAdd("Permissions", PermissionsNumber.ToString());
                }
            }

            while (await reader.ReadAsync())
            {
                var DiscordChannelId = reader.GetGuid(0);
                var RedisKey = $"channels:{DiscordChannelId.ToString()}";
                Data.TryAdd(RedisKey, "");
            }

            return Data;
        } catch (Exception error) {
            Console.WriteLine(error);
            return new Dictionary<string, string>();
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
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"SELECT id FROM users WHERE username = @username;",conn);
            cmd.Parameters.AddWithValue("username", Username);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return 0;
            }

            return reader.GetInt32(0);
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
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"SELECT id, name, color, position, separated FROM server_roles WHERE server_id = @server_id",conn);
            cmd.Parameters.AddWithValue("server_id", ServerId);
            await using var reader = await cmd.ExecuteReaderAsync();
            var Roles = new List<Role>();
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
            return new List<Role>();
        }
    }


    public async Task<List<Message>> SearchMessagesByWord(string? Search, Guid ChannelId, DateTime? cursorCreatedAt, Guid? cursorId)
    {
        try
        {
            if (Search == null) Search = "";

            string SQL = cursorCreatedAt is null && cursorId is null
                ? @"SELECT id, sender_id, message_content, created_at, edited
                    FROM server_messages
                    WHERE channel_id = @channel_id
                    AND message_content LIKE CONCAT('%', @search, '%')
                    ORDER BY created_at DESC, id DESC
                    LIMIT 50;"
                : @"SELECT id, sender_id, message_content, created_at, edited
                    FROM server_messages
                    WHERE channel_id = @channel_id
                    AND message_content LIKE CONCAT('%', @search, '%')
                    AND (created_at, id) < (@created_at, @id)
                    ORDER BY created_at DESC, id DESC
                    LIMIT 50;";

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
            var Messages = new List<Message>();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var sender_id = reader.GetInt32(1);
                var message_content = reader.GetString(2);
                var created_at = reader.GetDateTime(3);
                var edited = reader.GetBoolean(4);

                Messages.Add(new Message
                (
                    id,
                    sender_id,
                    message_content,
                    created_at,
                    edited
                ));
            }

            return Messages;
        } catch (Exception error) {
            Console.WriteLine(error);
            return new List<Message>();
        }
    }

    public int GetOnlineCountByServerId (Guid ServerId)
    {
        return ServerIdUserIdConns.ServerIdUsers[ServerId.ToString()].Count;
    }
}
