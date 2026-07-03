using System;
using Internal.Roles;
using Internal.Database;
using Npgsql;
class Server
{
    private readonly DatabaseHandler DBHandler;
    public Server(DatabaseHandler handler_)
    {
        DBHandler = handler_;
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

    public async Task<string> JoinServer(int ServerId, int JoinerId, string JoinerUsername)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var IsBannedCommand = new NpgsqlCommand("SELECT reason FROM server_bans WHERE user_id = @user_id AND server_id = @server_id;",conn);
            IsBannedCommand.Parameters.AddWithValue("user_id", JoinerId);
            IsBannedCommand.Parameters.AddWithValue("server_id", ServerId);
            await using var reader = await IsBannedCommand.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var banNote = reader.GetString(0);
                return $"You are banned from this server for {banNote}.";
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
            return "Joined Server Successfully.";
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
}
