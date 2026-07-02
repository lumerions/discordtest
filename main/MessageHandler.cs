
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Internal.WebSocketController;
using Internal.DatabaseHandler;
using Internal.RedisHandler;
using Npgsql;

public class MessagePayload
{
    public Guid MessageId {get; set;}
    public int UserId {get; set;}
}
class MessageHandler
{
    private readonly WebSocketSessionManager Manager;
    private readonly DatabaseHandler DBHandler;

    public MessageHandler(WebSocketSessionManager manager, DatabaseHandler databasehandler)
    {
        Manager = manager;
        DBHandler = databasehandler;
    }

    public async Task<bool> PrivateMessageUser(int MessagerUserId, int RecieverUserId, string Message, bool IsGroup)
    { 
        try
        {
            var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("INSERT INTO server_messages (sender_id, message_content, private_message) VALUES (@sender_id, @message_content, @private_message) RETURNING id;",conn);
            cmd.Parameters.AddWithValue("sender_id", MessagerUserId);
            cmd.Parameters.AddWithValue("message_content", Message);
            cmd.Parameters.AddWithValue("private_message", true);
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
                        UserId = MessagerUserId
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
            var conn = await DBHandler.GetConnection();
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
}