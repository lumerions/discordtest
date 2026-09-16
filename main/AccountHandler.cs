using Internal.Database;

namespace Internal.Accounts;

public class AccountHandler
{
    private readonly DatabaseHandler DBHandler;
    public AccountHandler (DatabaseHandler DBHandler_)
    {
        DBHandler = DBHandler_;
    }
    public async Task<bool> CreateNewSession (string OS, string Browser, string Location, int UserId, string NewSessionToken)
    {
        try
        {
            return await DBHandler.ExecuteAsync($"""
                INSERT INTO user_sessions (
                    user_location,
                    user_os,
                    user_id,
                    session_token,
                    expires_at,
                    user_browser
                )
                VALUES (
                    @user_location,
                    @user_os,
                    @user_id,
                    @session_token,
                    @expires_at,
                    @user_browser
                );
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("user_browser", Browser);
                cmd.Parameters.AddWithValue("user_location", Location);
                cmd.Parameters.AddWithValue("user_os", OS);
                cmd.Parameters.AddWithValue("user_id", UserId);
                cmd.Parameters.AddWithValue("session_token", NewSessionToken);
                cmd.Parameters.AddWithValue("expires_at", DateTime.UtcNow.AddDays(30));
            }) > 0;
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }

    public async Task<bool> DeleteUserSession (int Session_Id, int user_id, bool DeleteAllButId)
    {
        try
        {
            var DeleteSessionSql = "";

            if (DeleteAllButId)
            {
                DeleteSessionSql =  "DELETE FROM user_sessions WHERE id <> @Session_Id AND user_id = @user_id;";
            } else
            {
                DeleteSessionSql = "DELETE FROM user_sessions WHERE id = @Session_Id AND user_id = @user_id;";
            }

            return await DBHandler.ExecuteAsync(DeleteSessionSql, cmd =>
            {
                cmd.Parameters.AddWithValue("user_id", user_id);
                cmd.Parameters.AddWithValue("Session_Id", Session_Id);
            }) > 0;
        } catch (Exception error) {
            Console.WriteLine(error);
            return false;
        }
    }
}
