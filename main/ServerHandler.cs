using System;
using Internal.Roles;
using Internal.Database;
using Npgsql;

public record Role(
    int RoleId,
    string RoleName,
    int Color,
    long Position,
    bool Separated
);

public record Message(
    Guid id,
    int sender_id,
    string? message_content,
    DateTime created_at,
    bool edited,
    bool private_message
);


class Server
{
    private readonly DatabaseHandler DBHandler;
    public Server(DatabaseHandler handler_)
    {
        DBHandler = handler_;
    }

    public async Task<bool> DeleteServer(Guid ServerId)
    {
        try
        {
            return await DBHandler.ExecuteAsync($"""
                DELETE FROM servers WHERE id = @id
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", ServerId);
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
                    INSERT INTO server_channels (server_id, name, type, position, rules_channel)
                    SELECT
                        id,
                        'general',
                        'text',
                        0,
                        FALSE
                    FROM new_server

                    UNION ALL

                    SELECT
                        id,
                        'rules',
                        'text',
                        1,
                        TRUE
                    FROM new_server
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

    public async Task<string> JoinServer(int ServerId, int JoinerId, string JoinerUsername, Guid InviteCode)
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
                AND expires_at > NOW()
                AND (max_uses = 32000 OR uses < max_uses);
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
                if (await reader.ReadAsync())
                {
                    var isRevoked = reader.GetBoolean(0);

                    if (isRevoked )
                    {
                        return "Invites are paused for this server";
                    }
                else
                {
                    return "Invite is expired or invalid.";
                }
                }
            }

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
            ", conn);
            // TODO 
            // send a message in the server somewhere alerting other users
            // that this particular user joined in the first place
            joinServerCommand.Parameters.AddWithValue("user_id", JoinerId);
            joinServerCommand.Parameters.AddWithValue("server_id", ServerId);
            joinServerCommand.Parameters.AddWithValue("nickname", JoinerUsername);
            var result = await joinServerCommand.ExecuteScalarAsync();
            var success = result != null && result != DBNull.Value;
            var returnMessage = success ? "Joined Server Successfully." : "Could not join server please try again later.";
            return returnMessage;
        } catch(Exception err) {
            Console.WriteLine(err);
            return "Internal Server Error.";
        }
    }

    public async Task<bool> BanOrMuteUser(int ServerId, int BanId, int ModeratorId, string BanReason, DateTime? ExpiresAt, string TableName)
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

    public async Task<bool> KickUser(int ServerId, int UserId)
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

    public async Task<bool> ChangeServerNickname(int ServerId, int UserId, string Nickname)
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

    public async Task<bool> CreateServerChannel(int ServerId, string ChannelType, int Position)
    {
        try
        {
            return await DBHandler.ExecuteAsync(@"
                INSERT INTO server_channels (
                    server_id,
                    type,
                    position
                )
                VALUES (
                    @server_id,
                    @type,
                    @position
                );
            ", cmd =>
            {
                cmd.Parameters.AddWithValue("server_id", ServerId);
                cmd.Parameters.AddWithValue("type", ChannelType);
                cmd.Parameters.AddWithValue("position", Position);
            }).ContinueWith(t => t.Result > 0);
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<List<Role>> ViewRoles(int ServerId)
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
                var RoleId = reader.GetInt32(0);
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

            return Roles;
        } catch (Exception error) {
            Console.WriteLine(error);
            return new List<Role>();
        }
    }


    public async Task<List<Message>> SearchMessagesByWord(int ServerId, string? Search, Guid ChannelId, DateTime? cursorCreatedAt, Guid? cursorId)
    {
        try
        {
            string SQL = cursorCreatedAt is null && cursorId is null
                ? @"SELECT id, sender_id, message_content, created_at, edited, private_message
                    FROM server_messages
                    WHERE channel_id = @channel_id
                    AND message_content LIKE CONCAT('%', @search, '%')
                    ORDER BY created_at DESC, id DESC
                    LIMIT 50;"
                : @"SELECT id, sender_id, message_content, created_at, edited, private_message
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
                var private_message = reader.GetBoolean(5);

                Messages.Add(new Message
                (
                    id,
                    sender_id,
                    message_content,
                    created_at,
                    edited,
                    private_message
                ));
            }

            return Messages;
        } catch (Exception error) {
            Console.WriteLine(error);
            return new List<Message>();
        }
    }
}
