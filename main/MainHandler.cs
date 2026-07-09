using System;
using System.Threading.Tasks;
using Internal.Database;
using Npgsql;

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

public class MainHandler
{
    private readonly DatabaseHandler DBHandler;
    public MainHandler(DatabaseHandler databaseHandler)
    {
        DBHandler = databaseHandler;
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
}