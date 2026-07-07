
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Internal.WebSocketController;
using Internal.Database;
using Internal.Redis;
using Internal.Shared;
using Npgsql;
using System.IO.Pipelines;
using StackExchange.Redis;
using Internal.Roles;
using Microsoft.AspNetCore.Authorization;
using System.Collections.Concurrent;
using System.Data.SqlTypes;

namespace Internal.MessageHandler;

public class MessagePayload
{
    public Guid MessageId {get; set;}
    public int UserId {get; set;}
    public string Message {get; set;}
}

public class NewMessageHerePayload
{
    public bool NotificationUpdate {get; set;}
    public Guid ChannelId {get; set;}
}


public class NewMessagePayload
{
    public Guid MessageId {get; set;}
    public Guid ChannelId {get; set;}

    public string Message {get; set;}
}
class MessageHandler
{
    private readonly SharedMethods.WebSocketSessionManager Manager;
    private readonly DatabaseHandler DBHandler;
    private readonly SharedMethods Shared;

    public MessageHandler(SharedMethods.WebSocketSessionManager manager, DatabaseHandler databasehandler, SharedMethods shared_)
    {
        Manager = manager;
        DBHandler = databasehandler;
        Shared = shared_;
    }


    public async Task<bool> PrivateMessageUser(int MessagerUserId, int RecieverUserId, string Message, int ChannelId)
    { 
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("INSERT INTO server_messages (sender_id, message_content, private_message, channel_id) VALUES (@sender_id, @message_content, @private_message, @channel_id) RETURNING id;",conn);
            cmd.Parameters.AddWithValue("sender_id", MessagerUserId);
            cmd.Parameters.AddWithValue("message_content", Message);
            cmd.Parameters.AddWithValue("private_message", true);
            cmd.Parameters.AddWithValue("channel_id", ChannelId);

            var result = await cmd.ExecuteScalarAsync();

            if (result == null || result == DBNull.Value)
            {
                return false;
            }

            var MessageId = (Guid) result;

            async Task SendMessage(int UserId)
            {
                if (Manager.Users.TryGetValue(UserId.ToString(), out var UserSocket))
                { // this needs some more work but will have to do for now
                    var ResponseJSON = JsonSerializer.Serialize(new MessagePayload
                    {
                        MessageId = MessageId,
                        UserId = MessagerUserId,
                        Message = Message
                    });
                    var ResponseBytes = Encoding.UTF8.GetBytes(ResponseJSON);
                    await UserSocket.SendAsync(new ArraySegment<byte> (ResponseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }

            Task SendToMessager = SendMessage(MessagerUserId);
            Task SendToReciever = SendMessage(RecieverUserId);
            await Task.WhenAll(SendToMessager, SendToReciever);
            return true;
        } catch(Exception error)
        {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> EditMessage(string NewMessage, Guid MessageId)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("UPDATE server_messages SET edited = TRUE,message_content = @message_content WHERE id = @message_id RETURNING id;",conn);
            cmd.Parameters.AddWithValue("message_id", MessageId);
            cmd.Parameters.AddWithValue("message_content", NewMessage);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }

    public async Task<bool> SendMessageInServer(string NewMessage, int MessagerUserId, Guid ChannelId)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand(@"
                WITH inserted AS (
                    INSERT INTO server_messages (
                        sender_id,
                        message_content,
                        private_message,
                        channel_id
                    )
                    VALUES (
                        @sender_id,
                        @message_content,
                        @private_message,
                        @channel_id
                    )
                    RETURNING id, channel_id, sender_id
                )
                SELECT
                    inserted.id,
                    COALESCE(BIT_OR(sr.permissions), 0) AS permissions,
                    sc.server_id,
                    ARRAY_AGG(DISTINCT sc2.id ORDER BY sc2.position, sc2.id) AS channel_ids
                FROM inserted
                JOIN server_channels sc
                    ON sc.id = inserted.channel_id
                LEFT JOIN server_roles sr
                    ON sr.server_id = sc.server_id
                AND sr.user_id = inserted.sender_id
                JOIN server_channels sc2
                    ON sc2.server_id = sc.server_id
                GROUP BY inserted.id, sc.server_id;
            ", conn);
            cmd.Parameters.AddWithValue("sender_id", MessagerUserId);
            cmd.Parameters.AddWithValue("message_content", NewMessage);
            cmd.Parameters.AddWithValue("private_message", false);
            cmd.Parameters.AddWithValue("channel_id", ChannelId);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return false;
            }

            var MessageId = reader.GetGuid(0);
            var MessagePermissionNumber = reader.GetInt64(1);
            var ServerId = reader.GetGuid(2);
            HashSet<Guid> ChannelIds = reader.GetFieldValue<Guid[]>(3).ToHashSet();
            Permissions Perm = (Permissions) MessagePermissionNumber;
            var CanPing = (Perm & Permissions.MentionEveryone) != 0;

            if (NewMessage == "@everyone" || NewMessage == "@here")
            {
                if (!CanPing) return false;

                await reader.DisposeAsync();

                string SQL = NewMessage == "@here"
                ? @"INSERT INTO server_message_mentions (message_id, user_id)
                    SELECT
                        @message_id,
                        sm.user_id
                    FROM server_members sm
                    WHERE sm.server_id = @server_id
                    AND sm.user_id = ANY(@user_ids);"
                : @"INSERT INTO server_message_mentions (message_id, user_id)
                    SELECT
                        @message_id,
                        sm.user_id
                    FROM server_members sm
                    WHERE sm.server_id = @server_id;";

                await using var InsertNewNotificationsCmd = new NpgsqlCommand(SQL, conn);

                InsertNewNotificationsCmd.Parameters.AddWithValue("message_id", MessageId);
                InsertNewNotificationsCmd.Parameters.AddWithValue("server_id", ServerId);

                if (NewMessage == "@here")
                {
                    var AllOnlineUserIdsDict = new ConcurrentDictionary<string, byte>(     
                        Manager.Users.Keys.Select(id => new KeyValuePair<string, byte>(id, 0))
                    );

                    InsertNewNotificationsCmd.Parameters.AddWithValue("user_ids", Manager.Users.Keys.ToArray());
                }

                await InsertNewNotificationsCmd.ExecuteNonQueryAsync();     


                var MessageHereJson = JsonSerializer.Serialize(new NewMessageHerePayload
                {
                    NotificationUpdate = true,
                    ChannelId = ChannelId
                });

                await Shared.SendSocketMessage(null, MessageHereJson);
            }


            var allMessageJson = JsonSerializer.Serialize(new NewMessagePayload
            {
                Message = NewMessage,
                ChannelId = ChannelId,
                MessageId = MessageId
            });

            await Shared.SendSocketMessage(ChannelId, allMessageJson);
            
            return true;
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }
}