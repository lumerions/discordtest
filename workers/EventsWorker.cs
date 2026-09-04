using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Internal.Database;
using Internal.Shared;

namespace Workers.EventsWorker;

public class EventWorker
{
    private readonly SharedMethods Shared;
    private readonly DatabaseHandler DBHandler;
    private SharedMethods.ServerIdUserIdConnections ServerIdUserIdConnection;

    public EventWorker (SharedMethods Shared_, DatabaseHandler DBHandler_, SharedMethods.ServerIdUserIdConnections ServerIdUserIdConnection_)
    {
        DBHandler = DBHandler_;
        ServerIdUserIdConnection = ServerIdUserIdConnection_;
        Shared = Shared_;
    }

    public async Task RunEventAsync ()
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        while (await timer.WaitForNextTickAsync())
        {
            await using var conn = await DBHandler.GetConnection();
            await using var cmd = new NpgsqlCommand("""
                WITH deleted AS (
                    DELETE FROM server_events
                    WHERE end_time < NOW() - INTERVAL '1 hour'
                    RETURNING id
                ),
                deleted_interested AS (
                    DELETE FROM server_events_interested
                    WHERE server_event_id IN (SELECT id FROM deleted)
                )
                SELECT 
                    se.server_id,
                    sei.server_event_id,
                    sei.user_id,
                    se.event_topic
                FROM server_events se
                JOIN server_events_interested sei
                    ON sei.server_event_id = se.id
                WHERE se.start_time >= NOW() + INTERVAL '5 minutes'
                AND se.start_time <= NOW() + INTERVAL '10 minutes';
            """, conn);

            await using var EventsReader = await cmd.ExecuteReaderAsync();

            while (await EventsReader.ReadAsync())
            {
                var EventEndedServerId = EventsReader.GetGuid(0);
                var EventEndedEventId = EventsReader.GetGuid(1);
                var EventEndedInterestedId = EventsReader.GetInt32(2);
                var EventEndedEventTopic = EventsReader.GetString(3);
                var EventEndedUserIds = 
                ServerIdUserIdConnection.ServerIdUsers.Where(x => x.Key == EventEndedServerId.ToString())
                .ToDictionary(x => x.Key, x => x.Value);

                foreach (var (item, key) in EventEndedUserIds)
                {
                    await Shared.SendSocketMessage(null, $"{EventEndedEventTopic} is about to start!");
                }
            }
        }
    }
}