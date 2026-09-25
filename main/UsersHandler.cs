using System;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Npgsql;
using Internal.Database;
using Internal.Shared;

namespace Internal.Users;

public class DMConversationItem
{
    public Guid id {get; set;}
    public string? dm_pair_key {get; set;}
    public int owner_id {get; set;}
    public string? name {get; set;}
    public bool is_group {get; set;}
}

public class DMConversationGroupChatMemberList
{
    public int user_id {get; set;}
    public string username {get; set;}
    public string server_tag_id {get; set;}
    public string avatar_path {get; set;}
}

public class CreateGC
{
    public Dictionary<string, string> GCData {get; set;}
}

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
        await using var Cmd = new NpgsqlCommand($"SELECT id FROM {TypeInfoValue} WHERE file_name = @file_name;", Conn);
        var FileGetResult = await Cmd.ExecuteScalarAsync();

        if (FileGetResult == null)
        {
            return false;
        }

        await using var Transaction = await Conn.BeginTransactionAsync();

        async Task DeleteFileData ()
        {
            await using var DeleteFile = new NpgsqlCommand($"DELETE FROM {TypeInfoValue} WHERE file_name = @file_name;", Conn, Transaction);
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
        await using var Cmd = new NpgsqlCommand($"""
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

    public async Task<string> GetSentPendingFriendRequests (int SendId)
    {
        try
        {
            var Conn = await DBHandler.GetConnection();
            await using var Cmd = new NpgsqlCommand($"SELECT request_id FROM notifications WHERE sender_id = @SendId AND type = TRUE;", Conn);
            Cmd.Parameters.AddWithValue("SendId", SendId);
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

    public async Task<string> RejectFriendRequest (Guid NotificationId)
    {
        try
        {
            var Conn = await DBHandler.GetConnection();
            await using var Cmd = new NpgsqlCommand($"DELETE FROM notifications WHERE id = @NotificationId RETURNING id;", Conn);
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
            await using var Cmd = new NpgsqlCommand($"DELETE FROM notifications WHERE id = @NotificationId RETURNING type, sender_id;", Conn, Transaction);
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
            await using var WriteCmd = new NpgsqlCommand($"INSERT INTO friends (user_id, friend_id) VALUES (@UserId, @friend_id) RETURNING user_id;", Conn, Transaction);
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
            await using var WriteCmd = new NpgsqlCommand(
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

    public async Task<string> SetPersonalNote (int UserId, string PersonalNote)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            await using var WriteCmd = new NpgsqlCommand(
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
            await using var WriteCmd = new NpgsqlCommand(
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

    public async Task<List<DMConversationItem>> GetConversations (int UserId)
    {
        var DMConvos = new List<DMConversationItem>();
        var Conn = await DBHandler.GetConnection();

        try
        {
            await using var UserConversations = new NpgsqlCommand(
                $"""
                    SELECT 
                        c.id,
                        c.is_group,
                        c.name,
                        c.owner_id,
                        c.dm_pair_key,
                        m2.user_id,
                        u.username,
                        u.server_tag_id,
                        u.profile_status,
                        au.storage_path
                    FROM dm_conversation_members m
                    JOIN dm_conversations c
                        ON c.id = m.conversation_id
                    JOIN dm_conversation_members m2
                        ON m2.conversation_id = c.id
                    JOIN users u
                        ON u.id = m2.user_id
                    LEFT JOIN avatar_uploads au 
                        ON au.user_id = m2.user_id
                    WHERE m.user_id = @user_id
                    AND m.closed = FALSE;
                """
            , Conn);

            UserConversations.Parameters.AddWithValue("user_id", UserId);

            await using var Reader = await UserConversations.ExecuteReaderAsync();
            
            while (await Reader.ReadAsync()) 
            {
                DMConvos.Add(new DMConversationItem
                {
                    id = Reader.GetGuid(0),
                    is_group = Reader.GetBoolean(1),
                    name = Reader.IsDBNull(2) ? null : Reader.GetString(2),
                    owner_id = Reader.GetInt32(3),
                    dm_pair_key = Reader.IsDBNull(4) ? null : Reader.GetString(4)
                });
            }
        } catch (Exception err)
        {
           Console.WriteLine(err);
        }

        return DMConvos;
    }

    public async Task<bool> CloseConversation (int UserId, Guid[] Conversation_Ids)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            await using var UserCloseConversations = new NpgsqlCommand(
                $"""
                    UPDATE dm_conversation_members
                    SET closed = TRUE
                    WHERE conversation_id = ANY(@Conversation_Ids);
                """
            , Conn);

            UserCloseConversations.Parameters.AddWithValue("Conversation_Ids", Conversation_Ids);

            var CloseResult = await UserCloseConversations.ExecuteNonQueryAsync();
            
            return CloseResult > 0;
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return false;
        }
    }
    // GC announcements
    // "USER" changed the group name: "GROUPNAME"
    // "USER" changed the group icon.
    public async Task<List<DMConversationGroupChatMemberList>> GetMemberListDMS (Guid conversation_id)
    {
        var ConversationGCMembers = new List<DMConversationGroupChatMemberList>();
        var Conn = await DBHandler.GetConnection();

        try
        {
            await using var MemberListInformation = new NpgsqlCommand(
                $"""
                SELECT
                    dcm.user_id,
                    u.server_tag_id,
                    u.username,
                    au.storage_path
                FROM dm_conversation_members AS dcm
                JOIN users AS u
                    ON u.id = dcm.user_id
                LEFT JOIN avatar_uploads au 
                    ON au.user_id = dcm.user_id
                WHERE dcm.conversation_id = @conversation_id;
                """
            , Conn);

            MemberListInformation.Parameters.AddWithValue("conversation_id", conversation_id);

            await using var Reader = await MemberListInformation.ExecuteReaderAsync();
            
            while (await Reader.ReadAsync()) 
            {
                ConversationGCMembers.Add(new DMConversationGroupChatMemberList
                {
                    user_id = Reader.GetInt32(0),
                    server_tag_id = Reader.GetString(1),
                    username = Reader.GetString(2),
                    avatar_path = Reader.GetString(3)
                });
            }
        } catch (Exception err)
        {
           Console.WriteLine(err);
        }

        return ConversationGCMembers;
    }

    public async Task<bool> RemoveUserFromGroupChat (int UserId, Guid Conversation_Id)
    {
        var Conn = await DBHandler.GetConnection();

        try
        {
            await using var RemoveMemberFromConvo = new NpgsqlCommand(
                $"""
                    DELETE FROM dm_conversation_members AS m
                    USING dm_conversations AS c
                    WHERE m.conversation_id = c.id
                    AND c.id = @Conversation_Id
                    AND c.owner_id = @UserId;                
                """
            , Conn);

            RemoveMemberFromConvo.Parameters.AddWithValue("Conversation_Id", Conversation_Id);
            RemoveMemberFromConvo.Parameters.AddWithValue("UserId", UserId);

            var RemoveMemberFromConvoResult = await RemoveMemberFromConvo.ExecuteNonQueryAsync();
            
            return RemoveMemberFromConvoResult > 0;
        } catch (Exception err)
        {
           Console.WriteLine(err);
           return false;
        }
    }

    public async Task<bool> BulkWriteGroupIds (NpgsqlTransaction? Transaction, NpgsqlConnection Conn, int[] GroupIds, Guid ConversationId)
    {
        try
        {
            await using var AddMembersToGc = new NpgsqlCommand(
                """
                INSERT INTO dm_conversation_members (
                    conversation_id,
                    user_id
                )
                SELECT
                    @ConversationId,
                    unnest(@UserIds);
                """, Conn, Transaction);

            if (Transaction != null)
            {
                AddMembersToGc.Transaction = Transaction;
            }

            AddMembersToGc.Parameters.AddWithValue("UserIds", GroupIds);
            AddMembersToGc.Parameters.AddWithValue("ConversationId", ConversationId);

            var result = await AddMembersToGc.ExecuteNonQueryAsync();

            if (result != GroupIds.Length)
            {
                if (Transaction != null)
                {
                    await Transaction.RollbackAsync();
                }
                return false;
            }

            return true;
        }
        catch (Exception err)
        {
            Console.WriteLine(err);
            return false;
        }
    }

    public async Task<string> CreateGroupChat (int CreatorId, string GroupChatName, int[] GroupIds)
    {
        if (GroupIds.Length > 10) return "Too many ids to add.";

        var Conn = await DBHandler.GetConnection();
        await using var Transaction = await Conn.BeginTransactionAsync();

        try
        {
            await using var AddUserToGc = new NpgsqlCommand(
                $"""
                    INSERT INTO dm_conversations (
                        is_group,
                        name,
                        owner_id
                    )
                    VALUES (
                        TRUE,
                        @Name,
                        @OwnerId
                    )
                    RETURNING id;
                """
            , Conn, Transaction);

            AddUserToGc.Parameters.AddWithValue("OwnerId", CreatorId);
            AddUserToGc.Parameters.AddWithValue("Name", GroupChatName);

            var AddUserToGcResult = await AddUserToGc.ExecuteScalarAsync();

            if (AddUserToGcResult == null)
            {
                await Transaction.RollbackAsync();
                return "Update to conversation failed, please try again later.";
            }

            Guid ConversationId = (Guid) AddUserToGcResult;

            await using var AddOwnerToGc = new NpgsqlCommand(
                $"""
                    INSERT INTO dm_conversation_members (
                        conversation_id,
                        user_id,
                        closed
                    )
                    VALUES (
                        @ConversationId,
                        @OwnerId,
                        FALSE
                    )

                    RETURNING 1;
                """
            , Conn, Transaction);

            AddOwnerToGc.Parameters.AddWithValue("OwnerId", CreatorId);
            AddOwnerToGc.Parameters.AddWithValue("ConversationId", ConversationId);

            var AddOwnerToGcRes = await AddOwnerToGc.ExecuteScalarAsync();

            if (AddOwnerToGcRes is null)
            {
                await Transaction.RollbackAsync();
                return "Add to owner gc didn't work.";
            }

            await BulkWriteGroupIds(Transaction, Conn, GroupIds, ConversationId);
            await Transaction.CommitAsync();
            return "Success";
        } catch (Exception err)
        {
           Console.WriteLine(err);
           await Transaction.RollbackAsync();
           return "Internal Server Error.";
        }
    }
}