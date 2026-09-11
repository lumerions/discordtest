using System;
using System.Threading.Tasks;
using Workers.EventsWorker;
using Workers.FilesWorker;
public class StartWorkers
{
    public static async Task StartWorkersAsync(EventWorker EventWorker_, FilesWorker FileWorker_)
    {
        await EventWorker_.RunEventAsync();
        await FileWorker_.RunFilesWorkerAsync();
    }
}