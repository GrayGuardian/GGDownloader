using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GGDownloader
{
    public class DownloadQueue
    {
        public static DownloadQueue Default
        {
            get
            {
                if (_instance == null) _instance = new DownloadQueue();
                return _instance;
            }
        }
        private static DownloadQueue _instance;

        public const int DOWNLOAD_THREAD = 10;

        public bool InRunning => _cts != null && !_cts.IsCancellationRequested;
        public bool InPause { get; private set; }
        public int threadCount => _threadCount;
        private int _threadCount;
        public long curSize
        {
            get
            {
                try { return _downloadTasks.Sum((t) => t.downloadSize); }
                catch { }
                return 0;
            }
        }
        public long allSize
        {
            get
            {
                try { return _downloadTasks.Sum((t) => t.fileSize); }
                catch { }
                return 0;
            }
        }
        public double downloadSpeed
        {
            get
            {
                try { return _downloadTasks.Sum((t) => t.downloadSpeed); }
                catch { }
                return 0;
            }
        }
        public int downloadTaskCount => _downloadTasks.Count;
        public event Action<object, string> onError;
        public event Action<DownloadQueue, DownloadTask, EDownloadState> onTaskStateChange;
        public event Action<DownloadQueue, DownloadTask, DownloadChunk, EDownloadState> onTaskChunkStateChange;
        public event Action<DownloadQueue, DownloadTask, DownloadChunk, int> onDownloadProgress;
        public event Action<DownloadQueue, DownloadTask, DownloadChunk> onTaskChunkCreate;
        public event Action<DownloadQueue, int> onDownloadThreadQueueUpdate;
        public event Action<DownloadQueue, int, DownloadChunk> onDownloadThreadChunkChange;

        private LinkedList<DownloadTask> _downloadTasks = new LinkedList<DownloadTask>();

        private static object _lockObj = new object();
        
        private ConcurrentDictionary<int,bool> _checkTaskMap = new ConcurrentDictionary<int,bool>();
        private CancellationTokenSource _cts;

        public DownloadQueue(int threadCount = DOWNLOAD_THREAD)
        {
            _threadCount = threadCount;
        }

        public DownloadQueue Run()
        {
            _cts = new CancellationTokenSource();
            for (int i = 0; i < _threadCount; i++)
            {
                var index = i;
                _checkTaskMap.AddOrUpdate(index, false, (k, v) => false);
                Task.Run(async () => { await OnDownloadThread(index); },_cts.Token);
            }
            return this;
        }

        public void CheckTasks()
        {
            for (int i = 0; i < _threadCount; i++) _checkTaskMap.AddOrUpdate(i, true, (k, v) => true);
        }
            
        public async Task<DownloadTask> AddDownloadTask(string name, string url, string savePath, int chunkCount = 10)
        {
            var task = await CreateDownloadTask(name, url, savePath, chunkCount);
            lock(_lockObj)
            {
                _downloadTasks.AddLast(task);
            }
            return task;
        }
        public async Task<DownloadTask> AddDownloadTaskToFirst(string name, string url, string savePath, int chunkCount = 10)
        {
            var task = await CreateDownloadTask(name, url, savePath, chunkCount);
            lock (_lockObj)
            {
                _downloadTasks.AddFirst(task);
            }
            CheckTasks();
            return task;
        }
        private async Task<DownloadTask> CreateDownloadTask(string name, string url, string savePath, int chunkCount = 10)
        {
            var task = DownloadTask.Create();
            task.onError += OnError;
            task.onStateChange += OnTaskStateChange;
            task.onChunkStateChange += OnTaskChunkStateChange;
            task.onDownloadProgress += OnTaskDownloadProgress;
            task.onChunkCreate += OnTaskChunkCreate;
            await task.Init(this,name,url,savePath,chunkCount);
            return task;
        }

        public void Play()
        {
            if (!InPause) return;
            InPause = false;
        }
        public void Pause()
        {
            if (InPause) return;
            InPause = true;
        }
        public IEnumerable<DownloadTask> GetTaskByState(EDownloadState state)
        {
            return _downloadTasks.Where(t => (t.state & state) != 0);
        }
        private async Task OnDownloadThread(int index)
        {
            DownloadChunk chunk = null;
            while(InRunning)
            {
                if (InPause) continue;
                if (_checkTaskMap.TryGetValue(index, out var isCheck) && isCheck)
                {
                    onDownloadThreadQueueUpdate?.Invoke(this,index);
                    if(chunk != null)
                    {
                        chunk.inDownload = false;
                        chunk = null;
                        onDownloadThreadChunkChange?.Invoke(this,index,chunk);
                    }
                    _checkTaskMap.TryUpdate(index, false, true);
                }
                if(chunk == null)
                {
                    // 下载分片为空，重新找一个可以下载的分片
                    lock(_lockObj)
                    {
                        foreach(var task in _downloadTasks)
                        {
                            if (task.state != EDownloadState.Wait && task.state != EDownloadState.Downloading) continue;
                            var t_chunk = task.Chunks.FirstOrDefault(chunk => !chunk.inDownload && (chunk.state == EDownloadState.Wait || chunk.state == EDownloadState.Downloading));
                            if (t_chunk != null)
                            {
                                chunk = t_chunk;
                                chunk.inDownload = true;        // 在锁里面标记该分片已经被线程接管下载了
                                onDownloadThreadChunkChange?.Invoke(this, index, chunk);
                                break;
                            }
                        }
                    }
                }
                if (chunk == null) continue;        // 未找到则等待
                await chunk.Download(_cts);
                if(chunk.state == EDownloadState.Finished)
                {
                    // 分片下载完毕 清空下次循环重新查找
                    chunk.inDownload = false;
                    chunk = null;
                    onDownloadThreadChunkChange?.Invoke(this, index, chunk);
                }
            }   
        }
        private void OnError(object obj, string message)
        {
            onError?.Invoke(obj, message);
        }
        private void OnTaskStateChange(DownloadTask task, EDownloadState state)
        {
            onTaskStateChange?.Invoke(this, task, state);
        }
        private void OnTaskChunkStateChange(DownloadTask task, DownloadChunk chunk, EDownloadState state)
        {
            onTaskChunkStateChange?.Invoke(this, task, chunk, state);
        }
        private void OnTaskDownloadProgress(DownloadTask task, DownloadChunk chunk, int offset)
        {
            onDownloadProgress?.Invoke(this, task, chunk, offset);
        }
        private void OnTaskChunkCreate(DownloadTask task, DownloadChunk chunk)
        {
            onTaskChunkCreate?.Invoke(this, task, chunk);
        }
        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            foreach (var item in _downloadTasks) item.Dispose();
            _downloadTasks.Clear();
            onError = null;
            onTaskStateChange = null;
            onTaskChunkStateChange = null;
            onDownloadProgress = null;
            onTaskChunkCreate = null;
            onDownloadThreadQueueUpdate = null;
            onDownloadThreadChunkChange = null;
        }
    }
}
