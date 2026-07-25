using System;
using System.Threading.Tasks;
using Internal.Database;
using Internal.Shared;
using Internal.ServerCont;
using Npgsql;
using System.Net.WebSockets;
using System.Text.Json;

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

    public List<RoleItem> RoleData {get; set;}
}

public class Notification
{
    public int UserId;
}

public class FriendRequest : Notification
{
    public string Username;
}

public class MainHandler
{
    private readonly DatabaseHandler DBHandler;
    private readonly SharedMethods.WebSocketSessionManager Manager;
    private readonly ServersController ServerControll;
    public MainHandler(ServersController ServerController, DatabaseHandler databaseHandler, SharedMethods.WebSocketSessionManager manager)
    {
        DBHandler = databaseHandler;
        Manager = manager;
        ServerControll = ServerController;
    }

    public (bool IsOnline, WebSocket UserSocket) UserOnline (int UserId)
    {
        if (Manager.Users.TryGetValue(UserId.ToString(), out var Socket))
        {
           return (true, Socket);
        }

        return (true, null);
    }
    public async Task<ProfileInfo> GetProfileInfo(int UserId, int? ServerId)
    {
        string SQL = ServerId == null
            ? @"SELECT username, about_me, is_banned
                FROM users
                WHERE id = @id;"
            : @"SELECT username, about_me, is_banned
                FROM users
                WHERE id = @id;

                SELECT storage_path 
                FROM avatar_uploads
                WHERE user_id = @id;

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
                AND r.server_id = @server_id;";

        await using var conn = await DBHandler.GetConnection();
        await using var cmd = new NpgsqlCommand(SQL, conn);

        cmd.Parameters.AddWithValue("id", UserId);

        if (ServerId != null)
        {
            cmd.Parameters.AddWithValue("server_id", ServerId);
        }

        await using var Reader = await cmd.ExecuteReaderAsync();
        var UserName = "Deleted Account";
        var AboutMe = "";
        var Banned = true;
        var AvatarImage = "";
        var UserRoleData = new List<RoleItem>();

        if (await Reader.ReadAsync()) {
            UserName = Reader.GetString(0);
            AboutMe = Reader.IsDBNull(0) ? "" : Reader.GetString(1);
            Banned = Reader.GetBoolean(2);

            if (await Reader.NextResultAsync())
            {
                if (await Reader.ReadAsync())
                {
                    AvatarImage = Reader.IsDBNull(0) ? "" : Reader.GetString(0);
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
            }
        }
        
        var ProfileInformation = new ProfileInfo
        {
            Banned = Banned,
            Bio = AboutMe,
            ProfileName = UserName,
            AvatarImageUrl = AvatarImage,
            RoleData = UserRoleData
        };

        return ProfileInformation;
    }

    public async Task<bool> SendFriendRequest (int RecieverId, int SenderId, string SenderUsername)
    {
        var RequestSuccessful =  await DBHandler.ExecuteAsync(@"
            INSERT INTO notifications (
                sender_id,
                request_id,
                type
            )
            VALUES (
                @sender_id,
                @request_id,
                @type
            );
        ", cmd =>
        {
            cmd.Parameters.AddWithValue("sender_id", SenderId);
            cmd.Parameters.AddWithValue("request_id", RecieverId);
            cmd.Parameters.AddWithValue("type", true);
        }).ContinueWith(t => t.Result > 0);

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
}