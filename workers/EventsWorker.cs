using System;
using System.Threading.Tasks;
using Npgsql;
using Internal.Database;

namespace Workers.EventsWorker;

public class EventWorker
{
    private readonly DatabaseHandler DBHandler;

    public EventWorker (DatabaseHandler DBHandler_)
    {
        DBHandler = DBHandler_;
    }

    public async Task RunEventAsync ()
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromHours(1));

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
                    se.server_event_id,
                    sei.user_id
                FROM server_events se
                JOIN server_events_interested sei
                    ON sei.server_event_id = se.id
                WHERE se.end_time >= NOW() - INTERVAL '1 hour';
            """, conn);

            await using var EventsReader = await cmd.ExecuteReaderAsync();

            while (await EventsReader.ReadAsync())
            {
                var EventEndedServerId = EventsReader.GetGuid(0);
                var EventEndedEventId = EventsReader.GetGuid(1);
                var EventEndedInterestedId = EventsReader.GetInt32(2);
                // websocket support eventually
            }
        }
    }
}