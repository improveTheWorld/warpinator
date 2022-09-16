using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Timers;
using Common.Logging;
using Grpc.Core;
using Makaretu.Dns;
using iCode.Log;
using iCode.Extentions.IEnumerableExtentions;

namespace iShare
{
    public class Server
    {
        const string SERVICE_TYPE = "_warpinator._tcp";
        readonly DomainName ServiceDomain = new DomainName(SERVICE_TYPE+".local");

        public static Server current;

        public string DisplayName;
        public string UserName;
        public string Hostname;
        public ushort Port = 42000;
        public string UUID;
        public bool Running = false;
        public string SelectedInterface;

        public Dictionary<string, Remote> Remotes = new Dictionary<string, Remote>();
        public event EventHandler RemoteListUpdated;

        Grpc.Core.Server GrpcServer;
        readonly ServiceDiscovery ServiceDiscoverer;
        readonly MulticastService MultiCastServer;
        ServiceProfile serviceProfile;
        readonly ConcurrentDictionary<string, ServiceRecord> MdnsServices = new ConcurrentDictionary<string, ServiceRecord>();
        readonly ConcurrentDictionary<string, IPAddress> HostnameDict = new ConcurrentDictionary<string, IPAddress>();
        public Settings Settings;
        System.Timers.Timer PingTimer = new System.Timers.Timer(10_000);
        public event EventHandler ServerUpdated;

        public Server(Settings settings)
        {
            current = this;
            //try
            //{
            //    DisplayName = System.DirectoryServices.AccountManagement.UserPrincipal.Current.DisplayName ?? Environment.UserName;
            //}
            //catch(Exception e)
            //{
                DisplayName = Environment.UserName;
            //}
            Hostname = Environment.MachineName;
            UserName = Environment.UserName;

            //Load settings
            Settings = settings;
            if (!String.IsNullOrEmpty(Settings.UUID))
                UUID = Settings.UUID;
            else
            {
                UUID = Hostname.ToUpper() + "-" + String.Format("{0:X6}", new Random().Next(0x1000000));
                Settings.UUID = UUID;
            }
            if (String.IsNullOrEmpty(Settings.DownloadDir))
            {
                Settings.DownloadDir = Path.Combine(Utils.GetDefaultDownloadFolder(), "iShare");
                Directory.CreateDirectory(Settings.DownloadDir);
            }

            MultiCastServer = new MulticastService((ifaces) => ifaces.Where((iface) => SelectedInterface == null || iface.Id == SelectedInterface));
            MultiCastServer.UseIpv6 = false;
            MultiCastServer.IgnoreDuplicateMessages = true;
            ServiceDiscoverer = new ServiceDiscovery(MultiCastServer);
            PingTimer.Elapsed += (a, b) => PingRemotes();
            PingTimer.AutoReset = true;
        }

        public async Task Start(Settings newSettings = null )
        {
            if (newSettings != null)
            {
                Settings = newSettings;
            }
            this.Info("-- Starting server");
            Running = true;
            if (String.IsNullOrEmpty(Settings.NetworkInterface))
                SelectedInterface = null;
            else SelectedInterface = Settings.NetworkInterface;
            await StartGrpcServer(); //Also initializes authenticator for certserver
            CertServer.Start(Port);
            StartMDNS();
            PingTimer.Start();
            NotifyServerUpdated();
           
        }

        internal void NotifyServerUpdated()
        {
            ServerUpdated?.Invoke(this, null);
        }
        public async Task Stop()
        {
            if (!Running)
                return;
            Running = false;
            PingTimer.Stop();
            ServiceDiscoverer.Unadvertise(serviceProfile);
            MultiCastServer.Stop();
            CertServer.Stop();
            await GrpcServer.ShutdownAsync();
            NotifyServerUpdated();
            this.Info("-- Server stopped");
        }

        public async void Restart(Settings? newSettings = null)
        {
            await Stop();
            await Start(newSettings);
        }
       
        public void Rescan() => ServiceDiscoverer.QueryServiceInstances(SERVICE_TYPE);
        public void Reannounce() => ServiceDiscoverer.Announce(serviceProfile);

        private async Task StartGrpcServer()
        {
            KeyCertificatePair kcp = await Task.Run(Authenticator.GetKeyCertificatePair);
            GrpcServer = new Grpc.Core.Server() { 
                Services = { Warp.BindService(new GrpcService()) },
                Ports = { new ServerPort(Utils.GetLocalIPAddress().ToString(), Port, new SslServerCredentials(new List<KeyCertificatePair>() { kcp })) }
            };
            GrpcServer.Start();
            this.Info($"GRPC started at ports : "+GrpcServer.Ports.Serialize(",", (x)=>x.Port.ToString()));
        }

        private void StartMDNS(bool flush = false)
        {
            this.Debug("Starting mdns");
            
            foreach (var a in MulticastService.GetIPAddresses())
            {
                this.Debug($"MulticastService discovered IP address {a}");
            }
            MultiCastServer.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    this.Debug($"------------- MultiCastServer :On discovered NIC '{nic.Name}', id: {nic.Id}");
                }
            };
            ServiceDiscoverer.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            ServiceDiscoverer.ServiceInstanceShutdown += OnServiceInstanceShutdown;
            MultiCastServer.AnswerReceived += OnAnswerReceived;

            MultiCastServer.Start();
            ServiceDiscoverer.QueryServiceInstances(SERVICE_TYPE);
            var serviceIpDiscover = Utils.GetLocalIPAddress();
            var hostname = Utils.GetHostname();
            this.Log($" ServiceDiscovererat UUID ={UUID}, hostname = {hostname}, ip where server is discovered ={serviceIpDiscover}:{Port} ");
            serviceProfile = new ServiceProfile(UUID, SERVICE_TYPE, Port, new List<IPAddress> { serviceIpDiscover });
            serviceProfile.AddProperty("hostname", hostname );
            serviceProfile.AddProperty("type", flush ? "flush" : "real");
            ServiceDiscoverer.Advertise(serviceProfile);
            ServiceDiscoverer.Announce(serviceProfile);
        }

        private void PingRemotes()
        {
            foreach (var r in Remotes.Values)
            {
                if (r.Status == RemoteStatus.CONNECTED)
                    r.Ping();
            }
        }

        private void OnServiceInstanceDiscovered(object sender, ServiceInstanceDiscoveryEventArgs e)
        {
            var srvName = String.Join(".", e.ServiceInstanceName.Labels);
            this.Debug($"----------------ServiceDiscoverer: Service discovered: '{srvName}'");
            if (!MdnsServices.ContainsKey(e.ServiceInstanceName.ToCanonical().ToString()))
                MdnsServices.TryAdd(e.ServiceInstanceName.ToCanonical().ToString(), new ServiceRecord() { FullName = srvName });
        }
        
        private void OnServiceInstanceShutdown(object sender, ServiceInstanceShutdownEventArgs e)
        {
            this.Debug($" ----------------------ServiceDiscoverer: Service lost: '{e.ServiceInstanceName}'");
            string serviceId = e.ServiceInstanceName.ToString().Split('.')[0];
            if (Remotes.ContainsKey(serviceId))
            {
                var r = Remotes[serviceId];
                r.ServiceAvailable = false;
                r.NotifyRemoteStatusUpdated();
            }
        }

        private void OnAnswerReceived(object sender, MessageEventArgs e)
        {
            this.Debug($"------------------- MultiCastServer OnAnswerReceived Answer {e.Message.Id}:");
            var answers = e.Message.Answers.Concat(e.Message.AdditionalRecords).Where((r)=>r.Name.IsSubdomainOf(ServiceDomain) || r is AddressRecord);

            var servers = answers.OfType<SRVRecord>();
            foreach (var server in servers)
            {
                var srvName = String.Join(".", server.Name.Labels);
                this.Debug($"  Service '{srvName}' has hostname '{server.Target} and port {server.Port}'");
                if (!MdnsServices.ContainsKey(server.CanonicalName))
                    MdnsServices.TryAdd(server.CanonicalName, new ServiceRecord { FullName = srvName });
                MdnsServices[server.CanonicalName].Hostname = server.Target.ToString();
                if (HostnameDict.TryGetValue(server.Target.ToString(), out IPAddress addr))
                    MdnsServices[server.CanonicalName].Address = addr;
                MdnsServices[server.CanonicalName].Port = server.Port;
            }

            var addresses = answers.OfType<AddressRecord>();
            foreach (var address in addresses)
            {
                if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    this.Debug($"  Hostname '{address.Name}' resolves to {address.Address}");
                    HostnameDict.AddOrUpdate(address.Name.ToString(), address.Address, (a, b) => address.Address);
                    var svc = MdnsServices.Values.Where((s) => (s.Hostname == address.Name.ToString())).FirstOrDefault();
                    if (svc != null)
                        svc.Address = address.Address;
                }
            }

            var txts = answers.OfType<TXTRecord>();
            foreach (var txt in txts)
            {
                this.Debug("  Got strings: " + String.Join("; ", txt.Strings));
                MdnsServices[txt.CanonicalName].Txt = txt.Strings;
            }

            foreach (var svc in MdnsServices.Values)
            {
                if (!svc.resolved && svc.Address != null && svc.Txt != null)
                    OnServiceResolved(svc);
            }

            this.Debug($"-- End of OnAnswerReceived");
        }

        private void OnServiceResolved(ServiceRecord svc)
        {
            lock(this)
            {
                this.Trace("------------- OnServiceResolved ---------------------");
                {
                    string name = svc.FullName.Split('.')[0];
                    this.Debug("Resolved " + name);
                    if (name == UUID)
                    {
                        this.Debug("That's me - ignoring...");
                        svc.resolved = true;
                        return;
                    }
                   
                    var txt = new Dictionary<string, string>();
                    svc.Txt.ForEach((t) => { var s = t.Split('='); txt.Add(s[0], s[1]); });
                    // Ignore flush registration
                    if (txt.ContainsKey("type") && txt["type"] == "flush")
                    {
                        this.Trace("Ignoring flush registration");
                        return;
                    }
                    
                    svc.resolved = true; //TODO: support svc being updated
                    if (Remotes.ContainsKey(name))
                    {
                        Remote r = Remotes[name];
                        this.Debug($"Service already known, status: {r.Status}");
                        if (txt.ContainsKey("hostname"))
                            r.Hostname = txt["hostname"];
                        r.ServiceAvailable = true;
                        if (r.Status == RemoteStatus.DISCONNECTED || r.Status == RemoteStatus.ERROR)
                        {
                            //TODO: Update and reconnect
                        }
                        else r.NotifyRemoteStatusUpdated();
                        return;
                    }
                    
                    Remote remote = new Remote();
                    remote.Address = svc.Address;
                    if (txt.ContainsKey("hostname"))
                        remote.Hostname = txt["hostname"];
                    remote.Port = svc.Port;
                    remote.ServiceName = name;
                    remote.UUID = name;
                    remote.ServiceAvailable = true;

                    lock (Remotes)
                    {
                        if (!Remotes.ContainsKey(name))
                            Remotes.Add(name, remote);
                    }
                    NotifyRemoteListUpdated();
                    remote.Connect();
                    this.Trace("------------- End of OnServiceResolved ---------------------");
                }
            }
        }


   
    internal void NotifyRemoteListUpdated()
    {
        RemoteListUpdated?.Invoke(this, null);
    }

    private class ServiceRecord
        {
            public string FullName;
            public string Hostname;
            public IPAddress Address;
            public int Port;
            public List<string> Txt;
            public bool resolved = false;
        }
    }
}
