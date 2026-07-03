using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Internal.Database;
public class DatabaseHandler
{
    private readonly IConfiguration Configuration_;

    public DatabaseHandler(IConfiguration configuration)
    {
        Configuration_ = configuration;
    }
    public async Task<NpgsqlConnection> GetConnection()
    {
        var connectionUrl = Configuration_.GetConnectionString("Default");
        var conn = new NpgsqlConnection(connectionUrl);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<int> ExecuteAsync(string sql, Action<NpgsqlCommand> paramBuilder)
    {
        await using var conn = await GetConnection();
        await using var cmd = new NpgsqlCommand(sql, conn);

        paramBuilder(cmd);

        return await cmd.ExecuteNonQueryAsync();
    }
}