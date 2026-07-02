using StackExchange.Redis;

namespace Internal.Redis;
public class RedisHandler
{
    public ConnectionMultiplexer redis_;
    public RedisHandler()
    {
        redis_ = ConnectionMultiplexer.Connect("localhost:6379");
    }
    public IDatabase GetRedisDatabase()
    {
        return redis_.GetDatabase();
    }
}