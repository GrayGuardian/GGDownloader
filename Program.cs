using GGDownloader;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

internal class Program
{
    public static DownloadTask downloadTask;
    static void Main(string[] args)
    {
        DownloadQueue.Default.onError += (obj, message) => {
            Console.WriteLine($"发生异常 {obj.GetType()} {message}");
        };
        DownloadQueue.Default.onTaskStateChange += (queue,task,state) => {
            Console.WriteLine($"下载任务状态改变 > {task.name} {state}");
        };
        DownloadQueue.Default.onTaskChunkStateChange += (queue, task, chunk, state) => {
            Console.WriteLine($"下载分片状态改变 > {chunk.name} {state}");
        };
        DownloadQueue.Default.onDownloadProgress += (queue, task, chunk, offset) => {
            //Console.WriteLine($"{task.name}任务下载中 [{task.downloadSize}/{task.fileSize}] {task.downloadSpeed * 1000 / 1024}kb/s");
            Console.WriteLine($"下载队列任务 [{queue.curSize}/{queue.allSize}] {queue.downloadSpeed * 1000 / 1024}kb/s");

            Console.WriteLine($"任务进度:[{queue.GetTaskByState(EDownloadState.Finished | EDownloadState.Error).Count()}/{queue.downloadTaskCount}] 正在下载任务:{queue.GetTaskByState(EDownloadState.Downloading).Count()} 完成任务:{queue.GetTaskByState(EDownloadState.Finished).Count()} 空闲任务:{queue.GetTaskByState(EDownloadState.Wait).Count()} 暂停任务:{queue.GetTaskByState(EDownloadState.Pause).Count()} 异常任务:{queue.GetTaskByState(EDownloadState.Error).Count()}");
        };
        DownloadQueue.Default.onTaskChunkCreate += (queue, task, chunk) => {
            Console.WriteLine($"{task.name}下载分片创建{chunk.index} {chunk.from} - {chunk.to}  size: {chunk.size}");
        };
        DownloadQueue.Default.onDownloadThreadChunkChange += (queue, index, chunk) => {
            if(chunk != null)
                Console.WriteLine($"下载线程{index} 开始下载{chunk.task.name}分片{chunk.index}");
            else
                Console.WriteLine($"下载线程{index} 清理chunk");
        };
        DownloadQueue.Default.onDownloadThreadQueueUpdate += (queue, index) =>
        {
            Console.WriteLine($"下载线程{index}更新任务");
        };
        TTT();
        Console.ReadLine();
        downloadTask.Play();
        //DownloadQueue.Default.Play();
        Console.ReadLine();
    }

    static async Task TTT()
    {

        DownloadQueue.Default.Run();
        Console.WriteLine($"生成{DownloadQueue.Default.threadCount}个线程进行下载");
        //DownloadQueue.Default.AddDownloadTask("Game.7z", "http://127.0.0.1:8880/Game.7z", Path.Combine(AppContext.BaseDirectory, "Test"));
        //return;
        downloadTask = await DownloadQueue.Default.AddDownloadTask("jdk-25_windows-x64_bin.zip", "https://download.oracle.com/java/25/latest/jdk-25_windows-x64_bin.zip", Path.Combine(AppContext.BaseDirectory, "Test"));
        //downloadTask.Pause();
        //await Task.Delay(1000);
        DownloadQueue.Default.AddDownloadTask("jdk-21_windows-x64_bin.zip", "https://download.oracle.com/java/21/latest/jdk-21_windows-x64_bin.zip", Path.Combine(AppContext.BaseDirectory, "Test"));

        await Task.Delay(5000);
        downloadTask.Pause();
        //DownloadQueue.Default.Pause();

    }
}