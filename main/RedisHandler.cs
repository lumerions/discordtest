using StackExchange.Redis;

namespace Internal.RedisHandler;
class RedisHandler
{
    private readonly ConnectionMultiplexer redis_;
    public RedisHandler()
    {
        redis_ = ConnectionMultiplexer.Connect("localhost:6379");
    }
    public IDatabase GetRedisDatabase()
    {
        return redis_.GetDatabase();
    }
}