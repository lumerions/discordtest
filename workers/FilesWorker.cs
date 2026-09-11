using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Internal.Database;
using Internal.Shared;

namespace Workers.FilesWorker;

public class FilesWorker
{
    private readonly SharedMethods Shared;
    private readonly DatabaseHandler DBHandler;
    private HashSet<string> MarkedForDeletion = new HashSet<string>();

    private HashSet<string> DeleteFilePaths = new HashSet<string>();

    public FilesWorker (SharedMethods Shared_, DatabaseHandler DBHandler_)
    {
        DBHandler = DBHandler_;
        Shared = Shared_;
    }

    public async Task RunFilesWorkerAsync ()
    {
        string GetStoragePathForFullPath (string StoragePath)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), StoragePath);
        }

        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromDays(1));

        while (await timer.WaitForNextTickAsync())
        {
            string[] FinalFilePaths = [];

            foreach (var (key, value) in Shared.UploadsInfo())
            { // ill add the real paths eventually just temp for now 
                var UploadDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), value);
                string[] UploadDirectoryFilePaths = Directory.GetFiles(UploadDirectoryPath);

                for (int i = 0; i < UploadDirectoryFilePaths.Length; ++i)
                {
                    UploadDirectoryFilePaths[i] = GetStoragePathForFullPath(UploadDirectoryFilePaths[i]);
                }

                string[] FinalFilePathArr = new string[FinalFilePaths.Length + UploadDirectoryFilePaths.Length];

                Array.Copy(FinalFilePaths, 0, FinalFilePathArr, 0, FinalFilePaths.Length);
                Array.Copy(UploadDirectoryFilePaths, 0, FinalFilePathArr, FinalFilePaths.Length, UploadDirectoryFilePaths.Length);

                FinalFilePaths = FinalFilePathArr;
            }

            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("""
                SELECT storage_path
                FROM webhook_uploads
                WHERE storage_path = ANY(@Paths)

                UNION ALL

                SELECT storage_path
                FROM avatar_uploads
                WHERE storage_path = ANY(@Paths)

                UNION ALL

                SELECT storage_path
                FROM role_icon_uploads
                WHERE storage_path = ANY(@Paths);
            """, conn);

            cmd.Parameters.AddWithValue("Paths", FinalFilePaths);

            await using var FilesReader = await cmd.ExecuteReaderAsync();

            HashSet<string> FoundFilePaths = new();
            DeleteFilePaths = new HashSet<string>();

            while (await FilesReader.ReadAsync())
            {
                string FilePath = FilesReader.GetString(0);
                FoundFilePaths.Add(FilePath);
            }

            await FilesReader.DisposeAsync();

            foreach (var FinalFilePath in FinalFilePaths)
            {
                if (FoundFilePaths.Contains(FinalFilePath))
                {
                    continue;
                }

                try {

                    if (MarkedForDeletion.Contains(FinalFilePath))
                    {
                        if (File.Exists(FinalFilePath))
                        {
                            File.Delete(FinalFilePath);
                        }

                        MarkedForDeletion.Remove(FinalFilePath);
                        DeleteFilePaths.Add(FinalFilePath);
                    } else
                    {
                        MarkedForDeletion.Add(FinalFilePath);
                    }
                } catch (Exception err)
                {
                    Console.WriteLine(err);
                }
            }

            if (DeleteFilePaths.Count > 0)
            {
                await using var DeleteFileFromDb = new NpgsqlCommand("""
                    DELETE FROM webhook_uploads
                    WHERE storage_path = ANY(@Paths);

                    DELETE FROM avatar_uploads
                    WHERE storage_path = ANY(@Paths);

                    DELETE FROM role_icon_uploads
                    WHERE storage_path = ANY(@Paths);
                """, conn);

                DeleteFileFromDb.Parameters.AddWithValue("Paths", DeleteFilePaths);

                await DeleteFileFromDb.ExecuteNonQueryAsync();
            }
        }
    }
}