using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grpc.Core;
using iCode.Log;
using MimeTypeMap.List;

namespace iShare
{
    public enum TransferStatus
    {
        WAITING_PERMISSION, DECLINED, TRANSFERRING, PAUSED, STOPPED,
        FAILED, FINISHED, FINISHED_WITH_ERRORS
    }
    public enum TransferDirection {
        SEND, RECEIVE
    }
    
    public class Transfer
    {
        
        private enum FileType : int {
            FILE = 1, DIRECTORY = 2, SYMLINK = 3
        }
        private const int CHUNK_SIZE = 1024 * 512; //512 kB

        public TransferStatus Status;
        public TransferDirection Direction;
        public string RemoteUUID;
        public ulong StartTime;
        public ulong TotalSize;
        public ulong FileCount;
        public string SingleName = "";
        public string SingleMIME = "";
        public List<string> TopDirBaseNames;

        public bool OverwriteWarning = false;
        public List<string> FilesToSend;
        private List<string> ResolvedFiles;
        private string currentRelativePath;
        private string currentPath;
        private DateTime? currentFileDateTime;
        private bool cancelled = false;
        public event EventHandler TransferUpdated;
        public List<string> errors = new List<string>();

        public long BytesTransferred;
        public long BytesPerSecond;
        public long RealStartTime;
        public double Progress { get { return (double)BytesTransferred / TotalSize;  } }
        private long lastTicks;
        private FileStream currentStream;

        /***** SEND & RECEIVE *****/
        public void Stop(bool error = false)
        {
            Server.current.Remotes[RemoteUUID].StopTransfer(this, error);
            OnStopped(error);
        }

        public void OnStopped(bool error)
        {
            if (!error)
                Status = TransferStatus.STOPPED;
            if (Direction == TransferDirection.RECEIVE)
                StopReceiving();
            else StopSending();
            NotifyTransferUpdated();
        }

        public void MakeDeclined()
        {
            Status = TransferStatus.DECLINED;
            NotifyTransferUpdated();
        }

        public void NotifyTransferUpdated()
        { 
            TransferUpdated?.Invoke(this, null);
        }

        public string GetRemainingTime()
        {
            long now = DateTime.Now.Ticks;
            double avgSpeed = BytesTransferred / ((double)(now - RealStartTime) / TimeSpan.TicksPerSecond);
            int secondsRemaining = (int)((TotalSize - (ulong)BytesTransferred) / avgSpeed);
            return FormatTime(secondsRemaining);
        }

        string FormatTime(int seconds)
        {
            if (seconds > 60)
            {
                int minutes = seconds / 60;
                if (seconds > 3600)
                {
                    int hours = seconds / 3600;
                    minutes -= hours * 60;
                    return $"{hours}h {minutes}m";
                }
                seconds -= minutes * 60;
                return $"{minutes}m {seconds}s";
            }
            else if (seconds > 5)
                return $"{seconds}s";
            else
                return "a_few_seconds";
        }

        /***** SEND ******/
        public void PrepareSend()
        {
            ResolvedFiles = new List<string>();
            ResolveFiles();
            
            Status = TransferStatus.WAITING_PERMISSION;
            Direction = TransferDirection.SEND;
            StartTime = (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds();
            TotalSize = GetTotalSendSize();
            FileCount = (ulong)ResolvedFiles.Count;
            TopDirBaseNames = new List<string>(FilesToSend.Select((f) => Path.GetFileName(f)));
            if (FileCount == 1)
            {
                SingleName = Path.GetFileName(FilesToSend[0]);                
                SingleMIME = MimeTypeMap.List.MimeTypeMap.GetMimeType(Path.GetExtension(FilesToSend[0]).TrimStart('.')).First<string>();
            }
        }

        public async Task StartSending(IServerStreamWriter<FileChunk> stream)
        {
            Status = TransferStatus.TRANSFERRING;
            RealStartTime = DateTime.Now.Ticks;
            BytesTransferred = 0;
            cancelled = false;
            NotifyTransferUpdated();

            string f1 = FilesToSend[0];
            int parentLen = f1.TrimEnd(Path.DirectorySeparatorChar).LastIndexOf(Path.DirectorySeparatorChar);
            foreach (var f in ResolvedFiles)
            {
                if (cancelled) break;

                string p = f.Replace(Path.DirectorySeparatorChar, '/'); // For Linux & Android compatibility
                if (Directory.Exists(p))
                {
                    var chunk = new FileChunk()
                    {
                        RelativePath = p.Remove(0,parentLen+1),
                        FileType = (int)FileType.DIRECTORY,
                        FileMode = 493 //0755, C# doesn't have octal literals :(
                    };
                    await stream.WriteAsync(chunk);
                }
                else if (File.Exists(p))
                {
                    string relPath = p.Remove(0, parentLen + 1);

                    var fs = File.OpenRead(p);
                    long read = 0;
                    long length = fs.Length;
                    byte[] buf = new byte[CHUNK_SIZE];
                    bool firstChunk = true;
                    do //Send at least one chunk for empty files
                    {
                        int r = fs.Read(buf, 0, CHUNK_SIZE);
                        FileTime ftime = null;
                        if (firstChunk)
                        {
                            firstChunk = false;
                            ftime = new FileTime() {
                                Mtime = (ulong)new DateTimeOffset(File.GetLastWriteTime(p)).ToUnixTimeSeconds(),
                                MtimeUsec = (uint)File.GetLastWriteTime(p).Millisecond * 1000
                            };
                        }

                        var chunk = new FileChunk()
                        {
                            RelativePath = relPath,
                            FileType = (int)FileType.FILE,
                            FileMode = 420, //0644, C# doesn't have octal literals :(
                            Chunk = Google.Protobuf.ByteString.CopyFrom(buf, 0, r),
                            Time = ftime
                        };
                        await stream.WriteAsync(chunk);
                        read += r;
                        BytesTransferred += r;
                        long now = DateTime.Now.Ticks;
                        BytesPerSecond = (long)(r / ((double)(now - lastTicks) / TimeSpan.TicksPerSecond));
                        lastTicks = now;
                        NotifyTransferUpdated();
                    } while (read < length && !cancelled);
                }
            }
            if (!cancelled)
            {
                Status = TransferStatus.FINISHED;
                NotifyTransferUpdated();
            }
        }

        private void StopSending()
        {
            cancelled = true;
        }

        private void ResolveFiles()
        {
            foreach (var p in FilesToSend)
            {
                if (Directory.Exists(p))
                    resolveDirectory(p);
                else ResolvedFiles.Add(p);
            }

            void resolveDirectory(string dir)
            {
                ResolvedFiles.Add(dir);
                var dirs = Directory.GetDirectories(dir);
                foreach (var d in dirs)
                    resolveDirectory(d);
                var files = Directory.GetFiles(dir);
                ResolvedFiles.AddRange(files);
            }
        }

        private ulong GetTotalSendSize()
        {
            ulong total = 0;
            foreach (var f in ResolvedFiles)
            {
                if (File.Exists(f))
                    total += (ulong)new FileInfo(f).Length;
            }    
            return total;
        }

        /****** RECEIVE *******/

        public void PrepareReceive()
        {
            //TODO: Check enough space

            //Check if will overwrite
            if (Server.current.Settings.AllowOverwrite)
            {
                foreach (string p in TopDirBaseNames)
                {
                    var path = Path.Combine(Server.current.Settings.DownloadDir, Utils.SanitizePath(p));
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        OverwriteWarning = true;
                        break;
                    }
                }
            }

            Server.current.Remotes[RemoteUUID].IncomingTransferFlag = true;
            Server.current.Remotes[RemoteUUID].NotifyTransferListUpdated(this);

            if (Server.current.Settings.AutoAccept)
                StartReceiving();
        }

        public void StartReceiving()
        {
            this.Info("Transfer accepted");
            Status = TransferStatus.TRANSFERRING;
            RealStartTime = DateTime.Now.Ticks;
            NotifyTransferUpdated();
            Server.current.Remotes[RemoteUUID].StartReceiveTransfer(this);
        }

        public void DeclineTransfer()
        {
            this.Info("Transfer declined");
            Server.current.Remotes[RemoteUUID].DeclineTransfer(this);
            MakeDeclined();
        }

        public async Task<bool> ReceiveFileChunk(FileChunk chunk)
        {
            long chunkSize = 0;
            if (chunk.RelativePath != currentRelativePath)
            {
                // End of file
                CloseStream();
                if (currentFileDateTime.HasValue)
                    File.SetLastWriteTime(currentPath, currentFileDateTime.Value);
                currentFileDateTime = null;
                // Begin new file
                currentRelativePath = chunk.RelativePath;
                string sanitizedPath = Utils.SanitizePath(currentRelativePath);
                currentPath = Path.Combine(Server.current.Settings.DownloadDir, sanitizedPath);
                if (chunk.FileType == (int)FileType.DIRECTORY)
                    Directory.CreateDirectory(currentPath);
                else if (chunk.FileType == (int)FileType.SYMLINK)
                {
                    this.Warn("Symlinks not supported");
                    errors.Add("symlinks_not_supported");
                }
                else
                {
                    if (File.Exists(currentPath))
                        currentPath = HandleFileExists(currentPath);
                    if (chunk.Time != null)
                        currentFileDateTime = DateTimeOffset.FromUnixTimeSeconds((long)chunk.Time.Mtime).LocalDateTime;
                    try
                    {
                        currentStream = File.Create(currentPath);
                        var bytes = chunk.Chunk.ToByteArray();
                        await currentStream.WriteAsync(bytes, 0, bytes.Length);
                        chunkSize = bytes.Length;
                    } catch (Exception e)
                    {
                        this.Error($"Failed to open file for writing {currentRelativePath}", e);
                        errors.Add("failed_open_file" + currentRelativePath);
                        FailReceive();
                    }
                }
            }
            else
            {
                try
                {
                    var bytes = chunk.Chunk.ToByteArray();
                    await currentStream.WriteAsync(bytes, 0, bytes.Length);
                    chunkSize = bytes.Length;
                } catch (Exception e)
                {
                    this.Error($"Failed to write to file {currentRelativePath}: {e.Message}");
                    errors.Add(String.Format("failed_write_file", currentFileDateTime, e.Message));
                    FailReceive();
                }
            }
            BytesTransferred += chunkSize;
            long now = DateTime.Now.Ticks;
            BytesPerSecond = (long)(chunkSize / ((double)(now - lastTicks) / TimeSpan.TicksPerSecond));
            lastTicks = now;
            NotifyTransferUpdated();
            return Status == TransferStatus.TRANSFERRING;
        }

        public void FinishReceive()
        {
            this.Debug("Finalizing transfer");
            if (errors.Count > 0)
                Status = TransferStatus.FINISHED_WITH_ERRORS;
            else Status = TransferStatus.FINISHED;
            CloseStream();
            if (currentFileDateTime.HasValue)
                File.SetLastWriteTime(currentPath, currentFileDateTime.Value);
            NotifyTransferUpdated();
        }

        private void StopReceiving()
        {
            this.Trace("Stopping receiving");
            CloseStream();
            // Delete incomplete path
            try
            {
                File.Delete(currentPath);
            }
            catch (Exception e)
            {
                this.Warn("Could not delete incomplete file: " + e.Message);
            }
        }

        private void FailReceive()
        {
            //Don't overwrite other reason for stopping
            if (Status == TransferStatus.TRANSFERRING)
            {
                this.Debug("Receiving failed");
                Status = TransferStatus.FAILED;
                Stop(error: true); //Calls stopReceiving & informs other about error
            }
        }

        private string HandleFileExists(string path)
        {
            if (Server.current.Settings.AllowOverwrite)
            {
                this.Trace("Overwriting..");
                File.Delete(path);
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                var file = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                int i = 2;
                do
                {
                    path = Path.Combine(dir, $"{file} ({i}){ext}");
                    i++;
                } while (File.Exists(path));
                this.Trace("New path: " + path);
            }
            return path;
        }

        private void CloseStream()
        {
            if (currentStream != null)
            {
                try
                {
                    currentStream.Dispose();
                }
                catch { }
                currentStream = null;
            }
        }

        public string GetStatusString()
        {
            switch (Status) {
                case TransferStatus.WAITING_PERMISSION: return "waiting_for_permission";
                case TransferStatus.DECLINED: return "declined";
                case TransferStatus.TRANSFERRING: return "transferring";
                case TransferStatus.PAUSED: return "paused";
                case TransferStatus.STOPPED: return "stopped";
                case TransferStatus.FINISHED: return "finished";
                case TransferStatus.FINISHED_WITH_ERRORS: return "finished_errors";
                case TransferStatus.FAILED: return "failed";
                default: return "???";
            }
        }
    }
}
