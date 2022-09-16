using Grpc.Core;
using iCode.Log;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace iShare
{
    public enum RemoteStatus
    {
        CONNECTED,
        DISCONNECTED,
        CONNECTING,
        ERROR,
        AWAITING_DUPLEX
    }
    
    public class Remote
    {

        public IPAddress Address;
        public int Port;
        public string ServiceName;
        public string UserName;
        public string Hostname;
        public string DisplayName;
        public string UUID;
        public Bitmap Picture;
        public RemoteStatus Status;
        public bool ServiceAvailable;
        public bool IncomingTransferFlag = false;
        public List<Transfer> Transfers = new List<Transfer>();
        public event EventHandler RemoteUpdated;
        public event EventHandler<Transfer> TranferListUpdated;

        Channel channel;
        Warp.WarpClient client;

        public async void Connect()
        {
            this.Info($"Connecting to {Hostname}");
            Status = RemoteStatus.CONNECTING;
            NotifyRemoteStatusUpdated();
            if (!await Task.Run(ReceiveCertificateAsync))
            {
                Status = RemoteStatus.ERROR;
                NotifyRemoteStatusUpdated();
                return;
            }
            Console.WriteLine($"Certificate for {Hostname} received and saved");

            SslCredentials cred = new SslCredentials(Authenticator.GetRemoteCertificate(UUID));
            channel = new Channel(Address.ToString(), Port, cred);
            client = new Warp.WarpClient(channel);

            Status = RemoteStatus.AWAITING_DUPLEX;
            NotifyRemoteStatusUpdated();

            if (!await WaitForDuplex())
            {
                this.Error($"Couldn't establish duplex with {Hostname}");
                Status = RemoteStatus.ERROR;
                NotifyRemoteStatusUpdated();
                return;
            }

            Status = RemoteStatus.CONNECTED;

            //Get info
            var info = await client.GetRemoteMachineInfoAsync(new LookupName());
            DisplayName = info.DisplayName;
            UserName = info.UserName;

            // Get avatar
            try
            {
                var avatar = client.GetRemoteMachineAvatar(new LookupName());
                List<byte> bytes = new List<byte>();
                while (await avatar.ResponseStream.MoveNext())
                    bytes.AddRange(avatar.ResponseStream.Current.AvatarChunk);
                Picture = new Bitmap(new MemoryStream(bytes.ToArray()));
            } catch (Exception) {
                Picture = null;
            }

            NotifyRemoteStatusUpdated();
            this.Info($"Connection established with {Hostname}");
        }

        public async void Disconnect()
        {
            this.Info($"Disconnecting {Hostname}");
            await channel.ShutdownAsync();
            Status = RemoteStatus.DISCONNECTED;
        }

        public async void Ping()
        {
            try
            {
                await client.PingAsync(new LookupName() { Id = Server.current.UUID }, deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch (RpcException)
            {
                this.Debug($"Ping to {Hostname} failed");
                Status = RemoteStatus.DISCONNECTED;
                NotifyRemoteStatusUpdated();
            }
        }

        public void StartSendTransfer(Transfer t)
        {
            var opInfo = new OpInfo()
            {
                Ident = Server.current.UUID,
                Timestamp = t.StartTime,
                ReadableName = Server.current.Hostname
            };
            var req = new TransferOpRequest()
            {
                Info = opInfo,
                SenderName = Server.current.DisplayName,
                Receiver = UUID,
                Size = t.TotalSize,
                Count = t.FileCount,
                NameIfSingle = t.SingleName,
                MimeIfSingle = t.SingleMIME
            };
            req.TopDirBasenames.AddRange(t.TopDirBaseNames);
            client.ProcessTransferOpRequestAsync(req);
        }

        public async void StartReceiveTransfer(Transfer t)
        {
            var info = new OpInfo()
            {
                Ident = Server.current.UUID,
                Timestamp = t.StartTime,
                ReadableName = Server.current.Hostname
            };
            bool cancelled = false;
            try
            {
                using (var i = client.StartTransfer(info))
                {
                    while (await i.ResponseStream.MoveNext() && !cancelled)
                    {
                        var chunk = i.ResponseStream.Current;
                        cancelled = !await t.ReceiveFileChunk(chunk);
                    }
                }
                if (!cancelled)
                    t.FinishReceive();
            }
            catch (RpcException e)
            {
                if (e.StatusCode == StatusCode.Cancelled)
                {
                    this.Info("Transfer was reportedly cancelled");
                    t.Status = TransferStatus.STOPPED;
                }
                else
                {
                    this.Error("Error while receiving", e);
                    t.errors.Add("Error while receiving: " + e.Status.Detail);
                    t.Status = TransferStatus.FAILED;
                }
                t.NotifyTransferUpdated();
            }
        }

        public void DeclineTransfer(Transfer t)
        {
            var info = new OpInfo()
            {
                Ident = Server.current.UUID,
                Timestamp = t.StartTime,
                ReadableName = Server.current.Hostname
            };
            client.CancelTransferOpRequestAsync(info);
        }
        
        public void StopTransfer(Transfer t, bool error)
        {
            var info = new OpInfo()
            {
                Ident = Server.current.UUID,
                Timestamp = t.StartTime,
                ReadableName = Server.current.Hostname
            };
            var stopInfo = new StopInfo()
            {
                Error = error,
                Info = info
            };
            client.StopTransferAsync(stopInfo);
        }


        public void ProcessSendToTransfer(List<string> sendPaths)
        {
            if (sendPaths.Count != 0)
            {
                this.Info($"Send To: {Hostname}");
                Transfer t = new Transfer()
                {
                    FilesToSend = sendPaths,
                    RemoteUUID = UUID
                };
                
                t.PrepareSend();
                Transfers.Add(t);
                NotifyTransferListUpdated(t);
                StartSendTransfer(t);
            }
        }


        public void NotifyRemoteStatusUpdated()
        {
            RemoteUpdated?.Invoke(this, null);
        }

        internal void NotifyTransferListUpdated(Transfer t)
        {
            TranferListUpdated?.Invoke(this, t);
        }

        public string GetStatusString()
        {
            switch (Status)
            {
                case RemoteStatus.CONNECTED: return "Strings.connected";
                case RemoteStatus.DISCONNECTED: return "disconnected";
                case RemoteStatus.CONNECTING: return "connecting";
                case RemoteStatus.AWAITING_DUPLEX: return "awaiting_duplex";
                case RemoteStatus.ERROR: return "error";
                default: return "???";
            }
        }

        private async Task<bool> WaitForDuplex()
        {
            int tries = 0;
            while (tries < 10)
            {
                try
                {
                    var haveDuplex = await client.CheckDuplexConnectionAsync(new LookupName()
                    {
                        Id = Server.current.UUID,
                        ReadableName = Server.current.Hostname
                    });
                    if (haveDuplex.Response)
                        return true;
                }
                catch (RpcException e)
                {
                    this.Error("Connection interrupted while waiting for duplex", e);
                    return false;
                }
                this.Trace($"Duplex check attempt {tries}: No duplex");
                await Task.Delay(3000);
                tries++;
            }
            return false;
        }

        private async Task<bool> ReceiveCertificateAsync()
        {
            int tryCount = 0;
            byte[] received = null;
            UdpClient udp = new UdpClient();
            udp.Client.ReceiveTimeout = 5000;
            byte[] req = Encoding.ASCII.GetBytes(CertServer.Request);
            IPEndPoint endPoint = new IPEndPoint(Address, Port);
            while (tryCount < 3)
            {
                this.Trace($"Receiving certificate from {Address}, try {tryCount}");
                try
                {
                    await udp.SendAsync(req, req.Length, endPoint);
                    IPEndPoint recvEP = new IPEndPoint(0, 0);
                    var receivedData = await udp.ReceiveAsync();
                    received = receivedData.Buffer;
                    recvEP = receivedData.RemoteEndPoint;
                    this.Trace($"Received {received} from {recvEP} ");
                    if (recvEP.Equals(endPoint))
                    {
                        udp.Close();
                        break;
                    }
                }
                catch (Exception e)
                {
                    tryCount++;
                    this.Debug("ReceiveCertificate try " + tryCount + " failed: " + e.Message);
                    Thread.Sleep(1000);
                }
            }
            if (tryCount == 3)
            {
                this.Error($"Failed to receive certificate from {Hostname}");
                return false;
            }
            string base64encoded = Encoding.ASCII.GetString(received);
            Console.WriteLine($"Recived length ={base64encoded.Length}, data  = {base64encoded}") ;
            byte[] decoded = Convert.FromBase64String(base64encoded);
            
            if (!Authenticator.SaveRemoteCertificate(decoded, UUID))
            {
                this.Error(String.Format("error_groupcode", Hostname));
                return false;
            }
            return true;
        }
    }
}
