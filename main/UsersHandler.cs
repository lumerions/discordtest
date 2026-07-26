using System;
using System.IO;
using System.Linq;
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

        try
        {
            if (File.Exists(FileNamePath))
            {
                File.Delete(FileNamePath);
                var DeleteFile = new NpgsqlCommand($"DELETE FROM {TypeInfoValue} WHERE file_name = @file_name;", Conn, Transaction);
                DeleteFile.Parameters.AddWithValue("file_name", FileName);
                await DeleteFile.ExecuteNonQueryAsync();
                await Transaction.CommitAsync();
                return true;
            }

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
        var Cmd = new NpgsqlCommand($"SELECT file_name, created_at FROM avatar_uploads WHERE user_id = @user_id;", Conn);
        Cmd.Parameters.AddWithValue("user_id", UserId);
        var FileDateTimes = new Dictionary<string, DateTimeOffset>();
        var AvatarUploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "..", "controllers", "Avatar");
        var AvatarUploadFolderPath = Path.GetFullPath(AvatarUploadFolder);
        await using var Reader = await Cmd.ExecuteReaderAsync();

        while (await Reader.ReadAsync())
        {
            var Name = Reader.GetString(0);
            var CreationDate = Reader.GetFieldValue<DateTimeOffset>(1);
            var Filename = Path.GetFileName(Name);
            var FileNamePath = Path.Combine(AvatarUploadFolderPath, Filename);

            if (File.Exists(FileNamePath))
            {
                FileDateTimes.TryAdd(Filename, CreationDate);
            }
        }

        var MostRecentFiles = FileDateTimes.OrderByDescending(item => item.Value);
        var ItemNumber = 0;
        bool success = true;

        foreach (var (key, value) in MostRecentFiles)
        {
            if (ItemNumber >= 5)
            {
                if (!await DeleteImage(key, "avatar_uploads"))
                {
                    success = false;
                }
            }

            ItemNumber += 1;
        }
        
        return success;
    }
}