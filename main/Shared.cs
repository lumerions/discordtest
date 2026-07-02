using System.Collections.Concurrent;

namespace Internal.Shared;
public class WebSocketChannelIdConnections
{
    public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ChannelUsers = new();
}