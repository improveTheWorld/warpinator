using Grpc.Core;
using iShare;
using System.DirectoryServices.AccountManagement;
using Server = iShare.Server;

using Makaretu.Dns;
using System.Resources;
using Google.Protobuf.WellKnownTypes;


// See https://aka.ms/new-console-template for more information

Console.WriteLine("Hello, World!");
var netinterfaces = loadNetworkInterfacesList();


Server server = new Server(new Settings { AutoAccept = true, GroupCode = "warpinator", NetworkInterface = netinterfaces.Keys.Last() });
server.RemoteListUpdated += OnRemoteListUpdated;
server.ServerUpdated += OnServerUpdated;
//server.WatchByLogger();
await server.Start();
Console.ReadKey();

//-------------------------- end of main
void OnRemoteListUpdated(object s, EventArgs a)
{ }

void OnServerUpdated(object s, EventArgs a)
{ }
 
Dictionary<string, string>  loadNetworkInterfacesList()
{
    Dictionary<string, string> ifaceDict = new Dictionary<string, string>();
    var ifaces = MulticastService.GetNetworkInterfaces();
    ifaceDict.Clear();
    ifaceDict.Add("", "any");   
    int i = 1;
    foreach (var iface in ifaces)
    {
        ifaceDict.Add(iface.Id, iface.Name);
        
        i++;
    }
    
    return ifaceDict;
}
