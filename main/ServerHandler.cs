using Internal.Roles;
using Internal.Database;
using Npgsql;
class Server
{
    private readonly DatabaseHandler DBHandler;
    public Server(DatabaseHandler handler_)
    {
        DBHandler = handler_;
    }
    public async Task<bool> CreateServerRole(string RoleName, int Color, bool Seperated, int Position, long Permissions)
    {
        try
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("INSERT INTO server_roles (name, color, position, seperated, permissions) VALUES (@name, @color, @position, @seperated, @permissions) RETURNING id;",conn);
            cmd.Parameters.AddWithValue("name", RoleName);
            cmd.Parameters.AddWithValue("color", Color);
            cmd.Parameters.AddWithValue("position", Position);
            cmd.Parameters.AddWithValue("seperated", Seperated);
            cmd.Parameters.AddWithValue("permissions", Permissions);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        } catch(Exception err) {
            Console.WriteLine(err);
            return false;
        }
    }
}