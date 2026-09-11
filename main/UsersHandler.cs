using System;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using Npgsql;
using Internal.Database;
using Internal.Shared;

public class UsersHandler
{   
    private readonly DatabaseHandler DBHandler;
    private readonly SharedMethods Shared;
    public UsersHandler (DatabaseHandler DBHandler_, SharedMethods Shared_)
    { 
        DBHandler = DBHandler_;
        Shared = Shared_;
    }
    public async Task<bool> DeleteImage (string FileName, string UploadType)
    {
        var TypeInfo = Shared.UploadsInfo();

        if (!TypeInfo.TryGetValue(UploadType, out var TypeInfoValue))
        {
            return false;
        }

        var AvatarUploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "..", "controllers", "Avatar");
        var AvatarUploadsPath = Path.GetFullPath(AvatarUploadFolder);
        var Filename = Path.GetFileName(FileName);
        var FileNamePath = Path.Combine(AvatarUploadsPath, Filename);
        var Conn = await DBHandler.GetConnection();
        var Cmd = new NpgsqlCommand($"SELECT id FROM {TypeInfoValue} WHERE file_name = @file_name;", Conn);
        var FileGetResult = await Cmd.ExecuteScalarAsync();

        if (FileGetResult == null)
        {
            return false;
        }

        await using var Transaction = await Conn.BeginTransactionAsync();

        async Task DeleteFileData ()
        {
            var DeleteFile = new NpgsqlCommand($"DELETE FROM {TypeInfoValue} WHERE file_name = @file_name;", Conn, Transaction);
            DeleteFile.Parameters.AddWithValue("file_name", FileName);
            await DeleteFile.ExecuteNonQueryAsync();
        }

        try
        {
            if (File.Exists(FileNamePath))
            {
                File.Delete(FileNamePath);
                await DeleteFileData();
                await Transaction.CommitAsync();
                return true;
            } else
            {
                await DeleteFileData();
            }

            await Transaction.CommitAsync();
            return false;
        } catch (Exception err)
        {
            await Transaction.RollbackAsync();
            Console.WriteLine(err);
            return false;
        }
    }

    public async Task<bool> DeleteAllOldFiles (int UserId)
    {
        var Conn = await DBHandler.GetConnection();
        var Cmd = new NpgsqlCommand($"""
            SELECT file_name
            FROM avatar_uploads
            WHERE user_id = @user_id
            ORDER BY created_at DESC
            OFFSET 5;
            """, Conn);

        Cmd.Parameters.AddWithValue("user_id", UserId);

        var FileNamesList = new List<string>();
        var AvatarUploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "..", "controllers", "Avatar");
        var AvatarUploadFolderPath = Path.GetFullPath(AvatarUploadFolder);

        await using var Reader = await Cmd.ExecuteReaderAsync();

        while (await Reader.ReadAsync())
        {
            var Name = Reader.GetString(0);
            var Filename = Path.GetFileName(Name);
            var FileNamePath = Path.Combine(AvatarUploadFolderPath, Filename);

            if (File.Exists(FileNamePath))
            {
                FileNamesList.Add(Filename);
            }
        }

        bool success = true;

        foreach (var item in FileNamesList)
        {
            if (!await DeleteImage(item, "avatar_uploads"))
            {
                success = false;
            }
        }
        
        return success;
    }

    public async Task<string> RejectFriendRequest (Guid NotificationId)
    {
        try
        {
            var Conn = await DBHandler.GetConnection();
            var Cmd = new NpgsqlCommand($"DELETE FROM notifications WHERE id = @NotificationId RETURNING id;", Conn);
            Cmd.Parameters.AddWithValue("NotificationId", NotificationId);
            var Result = await Cmd.ExecuteScalarAsync();

            if (Result == null)
            {
                return "Notification not found.";
            }
            
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return "Internal Server Error.";
        }
    }

    public async Task<string> AcceptFriendRequest (int UserId, Guid NotificationId)
    {
        var Conn = await DBHandler.GetConnection();
        await using var Transaction = await Conn.BeginTransactionAsync();

        try
        {
            var Cmd = new NpgsqlCommand($"DELETE FROM notifications WHERE id = @NotificationId RETURNING type, sender_id;", Conn, Transaction);
            Cmd.Parameters.AddWithValue("NotificationId", NotificationId);
            await using var Reader = await Cmd.ExecuteReaderAsync();

            if (!await Reader.ReadAsync())
            {
                await Transaction.RollbackAsync();
                return "Notification not found.";
            }

            var NotificationType = Reader.GetString(0);
            var SenderId = Reader.GetInt32(1);
            await Reader.DisposeAsync();
            var WriteCmd = new NpgsqlCommand($"INSERT INTO friends (user_id, friend_id) VALUES (@UserId, @friend_id) RETURNING user_id;", Conn, Transaction);
            WriteCmd.Parameters.AddWithValue("UserId", UserId);
            WriteCmd.Parameters.AddWithValue("friend_id", SenderId);
            var WriteResult = await WriteCmd.ExecuteScalarAsync();

            if (WriteResult == null)
            {
                await Transaction.RollbackAsync();
                return "Failed to add friend, please try again.";
            }
            
            await Transaction.CommitAsync();
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           await Transaction.RollbackAsync();
           return "Internal Server Error.";
        }
    }

    public async Task<string> UnFriendUser (int UserId, int FriendId)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            var WriteCmd = new NpgsqlCommand(
                $"""
                DELETE FROM friends
                WHERE user_id = @UserId
                AND friend_id = @FriendId
                RETURNING user_id;
                """
            , Conn);

            WriteCmd.Parameters.AddWithValue("UserId", UserId);
            WriteCmd.Parameters.AddWithValue("friend_id", FriendId);
            var WriteResult = await WriteCmd.ExecuteScalarAsync();

            if (WriteResult == null)
            {
                return "Failed to remove friend, please try again.";
            }
            
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return "Internal Server Error.";
        }
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetAllFriends (int UserId)
    {
        var FriendData = new Dictionary<string, DateTimeOffset>();

        try
        {
            var Conn = await DBHandler.GetConnection();
            var Cmd = new NpgsqlCommand($"""
            SELECT
                CASE
                    WHEN user_id = @UserId THEN friend_id
                    ELSE user_id
                END AS friend_id,
                created_at
            FROM friends
            WHERE user_id = @UserId OR friend_id = @UserId;
            """, Conn);

            Cmd.Parameters.AddWithValue("UserId", UserId);

            await using var reader = await Cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var FriendId = reader.GetInt32(0);
                var FriendedAt = reader.GetFieldValue<DateTimeOffset>(1);
                FriendData.Add(FriendId.ToString(), FriendedAt);
            }
            
            return FriendData;
        } catch (Exception err)
        {
           Console.WriteLine(err);
           FriendData.Add("error", DateTimeOffset.UtcNow);
           return FriendData;
        }
    }

    public async Task<string> SetPersonalNote (int UserId, string PersonalNote)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            var WriteCmd = new NpgsqlCommand(
                $"""
                    INSERT INTO personal_profile_note (user_id, personal_note)
                    VALUES (@user_id, @personal_note)
                    RETURNING id;
                """
            , Conn);

            WriteCmd.Parameters.AddWithValue("user_id", UserId);
            WriteCmd.Parameters.AddWithValue("personal_note", PersonalNote);
            var WriteResult = await WriteCmd.ExecuteScalarAsync();

            if (WriteResult == null)
            {
                return "Failed to set personal note, please try again.";
            }
            
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return "Internal Server Error.";
        }
    }

    public async Task<string> ReportMessage (int UserId, int UserIdReported, string MessageSent)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            var WriteCmd = new NpgsqlCommand(
                $"""
                    INSERT INTO user_message_reports (reported_message, user_id_reporter, user_id_reported)
                    VALUES (@reported_message, @user_id_reporter, @user_id_reported)
                    RETURNING id;
                """
            , Conn);

            WriteCmd.Parameters.AddWithValue("user_id_reporter", UserId);
            WriteCmd.Parameters.AddWithValue("user_id_reported", UserIdReported);
            WriteCmd.Parameters.AddWithValue("reported_message", MessageSent);

            var WriteResult = await WriteCmd.ExecuteScalarAsync();

            if (WriteResult == null)
            {
                return "Failed to report message, please try again.";
            }
            
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return "Internal Server Error.";
        }
    }
}