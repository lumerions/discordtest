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
}