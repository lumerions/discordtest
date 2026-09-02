using System;
using System.Threading.Tasks;
using Workers.EventsWorker;

public class StartWorkers
{
    public static async Task StartWorkersAsync(EventWorker EventWorker_)
    {
        await EventWorker_.RunEventAsync();
    }
}