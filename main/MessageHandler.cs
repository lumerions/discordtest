
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Internal.WebSocketController;
using Internal.Database;
using Internal.Redis;
using Internal.Shared;
using Npgsql;

namespace Internal.MessageHandler;

public class MessagePayload
{
    public Guid MessageId {get; set;}
    public int UserId {get; set;}
    public string Message {get; set;}
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
            await using var cmd = new NpgsqlCommand("INSERT INTO server_messages (sender_id, message_content, private_message, channel_id) VALUES (@sender_id, @message_content, @private_message, @channel_id) RETURNING id;",conn);
            cmd.Parameters.AddWithValue("sender_id", MessagerUserId);
            cmd.Parameters.AddWithValue("message_content", NewMessage);
            cmd.Parameters.AddWithValue("private_message", false);
            cmd.Parameters.AddWithValue("channel_id", ChannelId);
            var result = await cmd.ExecuteScalarAsync();
            var success = result != null && result != DBNull.Value;
            if (success)
            {
                Guid MessageId = (Guid) result;

                var allMessageJson = JsonSerializer.Serialize(new NewMessagePayload
                {
                    Message = NewMessage,
                    ChannelId = ChannelId,
                    MessageId = MessageId
                });

                await Shared.SendSocketMessage(ChannelId, allMessageJson);
            }

            return success;
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }
}