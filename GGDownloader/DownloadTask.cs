using GGDownloader.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GGDownloader
{
    public enum EDownloadState
    {
        Wait = 0,               // 等待
        Downloading = 1 << 1,        // 下载中 
        Error = 1 << 2,              // 异常
        Finished = 1 << 3,            // 完成
        Pause = 1 << 5,              // 暂停
    }
    public class DownloadTask : IRefrence
    {
        public const string CHUNK_FILE_EXT = "chunk";
        public static DownloadTask Create()
        {
            var task = TaskPool.Get<DownloadTask>();
            return task;
        }
        public DownloadQueue downloadQueue { get; private set; }
        public string name { get; private set; }
        public string url { get; private set; }
        public string savePath { get; private set; }
        public string filePath { get; private set; }
        public long fileSize { get; private set; }
        public long downloadSize {
            get
            {
                try
                {
                    if (state == EDownloadState.Finished) return fileSize;
                    return Chunks.Sum((t) => t.curSize);
                }
                catch { }
                return 0;
            }
        }
        /// <summary>
        /// 下载速率 单位 字节/毫秒
        /// </summary>
        public double downloadSpeed
        {
            get
            {
                try
                {
                    if (state == EDownloadState.Finished) return 0;
                    return Chunks.Sum((t) => t.downloadSpeed);
                }
                catch { }
                return 0;
            }
        }
        public EDownloadState state { get; private set; }

        public event Action<object,string> onError;
        public event Action<DownloadTask, EDownloadState> onStateChange;
        public event Action<DownloadTask, DownloadChunk, EDownloadState> onChunkStateChange;
        public event Action<DownloadTask, DownloadChunk, int> onDownloadProgress;
        public event Action<DownloadTask, DownloadChunk> onChunkCreate;

        public List<DownloadChunk> Chunks = new List<DownloadChunk>();
        public void Init()
        {
        }
        public async Task Init(DownloadQueue queue,string name, string url, string savePath, int chunkCount = 10)
        {
            this.downloadQueue = queue;
            this.name = name;
            this.url = url;
            this.savePath = savePath;
            SetState(EDownloadState.Wait);
            var fileSize = await NetUtil.GetFileSize(url);
            if (fileSize <= 0)
            {
                // 未获取到文件信息 报错并设置异常
                OnError(this,$"Url is Null url:{url}");
                return;
            }
            this.fileSize = fileSize;
            this.filePath = Path.Combine(savePath, name);
            if (File.Exists(filePath))
            {
                if(new FileInfo(filePath).Length == fileSize)
                {
                    // 已存在文件，则不下载，直接上报完成
                    SetState(EDownloadState.Finished);
                    return;
                }
                else File.Delete(filePath);     // 文件异常，删除重新下载
            }
            // 计算分片信息
            var count = chunkCount;
            var rcount = (int)Math.Ceiling(fileSize / (decimal)DownloadChunk.READ_BYTES_COUNT);
            if (rcount < count) count = rcount;     // 单次读取字节判断，防止分片数过大
            if (fileSize < count) count = 1;         // 防止极限情况下，分片数比下载字节大
            
            // 查询是否需要断点续传
            var chunkFiles = Directory.GetFiles(savePath, $"{name}.*.{CHUNK_FILE_EXT}");
            if (chunkFiles.Length > 0)
            {
                // 校验分片文件是否正确
                int i = 0;
                if (chunkFiles.All((file) => Path.GetFileName(file) == $"{name}.{i++}.{CHUNK_FILE_EXT}")) count = chunkFiles.Length;  // 分片数量使用文件数量
                else
                {
                    foreach (var file in chunkFiles) File.Delete(file);     // 分片文件有误，全部清理重新下载
                }
            }
            // 生成分片信息
            // 提前生成分片文件，防止初始化分片时遇到断点续传导致分片数量异常
            for (int i = 0; i< count;i++)
            {
                var path = GetChunkFilePath(i);
                if (!File.Exists(path)) File.Create(path).Dispose();
            }
            long size = (long)Math.Floor(fileSize / (decimal)count);
            long from = 0;
            for (int i = 0;i < count; i++)
            {
                long to = i == count - 1 ? fileSize : size * (i + 1);
                to -= 1;
                var chunk = DownloadChunk.Create();
                chunk.onError += OnError;
                chunk.onStateChange += OnChunkStateChange;
                chunk.onDownloadProgress += OnChunkDownloadProgress;
                chunk.Init(this, i, from, to);
                Chunks.Add(chunk);
                onChunkCreate?.Invoke(this,chunk);
                from = to + 1;
            }
        }

        private void OnChunkStateChange(DownloadChunk chunk, EDownloadState state)
        {
            onChunkStateChange?.Invoke(this, chunk, state);
            if (state == EDownloadState.Downloading)
            {
                if(this.state == EDownloadState.Wait) SetState(EDownloadState.Downloading);
            }
            else if(state == EDownloadState.Finished)
            {
                if(Chunks.All((chunk) => chunk.state == EDownloadState.Finished)) MergeChunks();
            }
        }
        private void OnChunkDownloadProgress(DownloadChunk chunk, int offset)
        {
            onDownloadProgress?.Invoke(this, chunk, offset);
        }
        public void OnEnable()
        {
        }
        public void Play()
        {
            if (this.state != EDownloadState.Pause) return;
            SetState(EDownloadState.Wait);
            foreach (var chunk in Chunks) chunk.SetState(EDownloadState.Wait);
            this.downloadQueue.CheckTasks();
        }
        public void Pause()
        {
            if (this.state != EDownloadState.Wait && this.state != EDownloadState.Downloading) return;
            SetState(EDownloadState.Pause);
            foreach (var chunk in Chunks) chunk.SetState(EDownloadState.Pause);
            this.downloadQueue.CheckTasks();
        }
        public void MergeChunks()
        {
            // 合并所有分片文件
            try
            {
                var tempPath = $"{filePath}.temp";
                if(File.Exists(tempPath)) File.Delete(tempPath);
                using (FileStream fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    fs.Seek(0, SeekOrigin.End);
                    foreach (var chunk in Chunks)
                    {
                        var bytes = File.ReadAllBytes(chunk.filePath);
                        fs.Write(bytes, 0, bytes.Length);
                        File.Delete(chunk.filePath);
                    }
                    fs.Close();
                }
                File.Move(tempPath, filePath);

                SetState(EDownloadState.Finished);
            }
            catch(Exception e)
            {
                OnError(this, $"Merge Error:{e.Message}");
            }
        }
        public void OnError(object obj,string message)
        {
            SetState(EDownloadState.Error);
            onError?.Invoke(obj, message);
        }
        public void SetState(EDownloadState state)
        {
            if(this.state == state) return;
            this.state = state;
            onStateChange?.Invoke(this,state);
        }
        public string GetChunkFilePath(int chunkIndex)
        {
            return Path.Combine(savePath, $"{name}.{chunkIndex}.{CHUNK_FILE_EXT}");
        }
        public void OnDispose()
        {
            foreach (var chunk in Chunks) chunk.Dispose();
            Chunks.Clear();
            onError = null;
            onStateChange = null;
            onChunkStateChange = null;
            onDownloadProgress = null;
            onChunkCreate = null;
        }
        public void Dispose()
        {
            TaskPool.Recycle(this);
        }
    }

    public class DownloadChunk : IRefrence
    {
        public const int READ_BYTES_COUNT = 8 * 1024;
        public static DownloadChunk Create()
        {
            var chunk = TaskPool.Get<DownloadChunk>();
            return chunk;
        }
        public DownloadTask task { get; private set; }
        public int index { get; private set; }
        public string filePath { get; private set; }
        public long from { get; private set; }
        public long to { get; private set; }
        public long size { get; private set; }
        public EDownloadState state { get; private set; }
        public bool inDownload { get; set; }
        public string name => $"{task.name}[{index}]";
        public long curSize
        {
            get
            {
                try
                {
                    if (state == EDownloadState.Finished) return size;
                    if (_fs != null) return _fs.Length;
                } catch { }
                return 0;
            }
        }
        /// <summary>
        /// 下载速率 单位 字节/毫秒
        /// </summary>
        public double downloadSpeed
        {
            get
            {
                if (state != EDownloadState.Downloading) return 0;
                return _downloadSpeed;
            }
            set => _downloadSpeed = value;
        }
        public double _downloadSpeed;

        private HttpWebRequest _request;
        private HttpWebResponse _response;
        private Stream _ns;
        private FileStream _fs;
        private byte[] _rbytes;
        private int _rSize;
        private Stopwatch _stopWatch;
        private long offsetSize;

        public event Action<object,string> onError;
        public event Action<DownloadChunk,EDownloadState> onStateChange;
        public event Action<DownloadChunk, int> onDownloadProgress;
        public void Init()
        {
        }
        public void Init(DownloadTask task,int index, long from, long to)
        {
            this.task = task;
            this.index = index;
            this.from = from;
            this.to = to;
            this.size = to - from + 1;
            this.filePath = task.GetChunkFilePath(index);
            // 计算分片详细信息
            if (File.Exists(filePath))
            {
                var fileSize = new FileInfo(filePath).Length;
                if (fileSize == size)
                {
                    // 已存在文件，则不下载，但还需要在Download内上报完成
                }
                else if(fileSize > size)
                    File.Delete(filePath);          // 文件异常过大，删除
                else
                {
                    this.from += fileSize;        // 文件已下载部分
                }
            }
        }
        public void OnEnable()
        {
        }
        public async Task Download(CancellationTokenSource cts)
        {
            if (_request == null)
            {
                try
                {
                    // 下载准备
                    _request = (HttpWebRequest)HttpWebRequest.Create(task.url);
                    _request.AddRange(from, to);
                    _response = (HttpWebResponse)_request.GetResponse();
                    _ns = _response.GetResponseStream();
                    _rbytes = new byte[READ_BYTES_COUNT];
                    _rSize = 0;
                    _fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    _fs.Seek(0, SeekOrigin.End);
                    _stopWatch = Stopwatch.StartNew();
                }
                catch (Exception ex)
                {
                    OnError(this, $"Chunk DownloadInfo Error:{ex.Message}");
                }
            }
            if (state != EDownloadState.Downloading)
            {
                _stopWatch.Restart();
                offsetSize = 0;
                SetState(EDownloadState.Downloading);
            }
            //Console.WriteLine($"{task.name}分片{index}下载");
            if (_fs.Length >= size)
            {
                //Console.WriteLine($"{task.name}分片{index}下载完毕 {_fs.Length}");
                CloseDownload();
                SetState(EDownloadState.Finished);
                return;
            }
            try
            {
                _rSize = _ns.Read(_rbytes, 0, _rbytes.Length);
                if (_rSize > 0)
                {

                    _fs.Write(_rbytes, 0, _rSize);
                    // 计算速率
                    var ms = _stopWatch.ElapsedMilliseconds;
                    offsetSize += _rSize;
                    if (ms > 200)
                    {
                        downloadSpeed = offsetSize == 0 ? 0 : offsetSize / (double)ms;
                        _stopWatch.Restart();
                        offsetSize = 0;
                    }
                    //Console.WriteLine($"{task.name}分片{index}下载中 [{curSize}/{size}] {downloadSpeed} {_rSize}");
                    onDownloadProgress?.Invoke(this, _rSize);
                }
            }
            catch (Exception e)
            {
                OnError(this, $"Chunk Download Error:{e.Message}");
            }
        }
        public void OnError(object obj,string message)
        {
            SetState(EDownloadState.Error);
            onError?.Invoke(obj,message);
        }
        public void SetState(EDownloadState state)
        {
            if (this.state == state) return;
            this.state = state;
            onStateChange?.Invoke(this, state);
        }
        public void CloseDownload ()
        {
            _fs?.Close();
            _fs = null;
            _ns?.Close();
            _ns = null;
            _response?.Close();
            _response = null;
            _request?.Abort();
            _request = null;
            _stopWatch?.Stop();
            _stopWatch = null;
            offsetSize = 0;
            downloadSpeed = 0;
        }
        public void OnDispose()
        {
            inDownload = false;
            CloseDownload();
            onError = null;
            onStateChange = null;
            onDownloadProgress = null;
        }
        public void Dispose()
        {
            TaskPool.Recycle(this);
        }
    }
}
