using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using iCode.Log;

namespace iShare
{
    class CertServer
    {
        public const string Request = "REQUEST";
        static int Port;

        static UdpClient client;
        static Thread serverThread;
        static bool running = false;
        
        public static void Start(int port)
        {
            Port = port;
            if (client != null)
                client.Close();
            running = true;
            serverThread = new Thread(() => Run());
            serverThread.Start();
        }

        public static void Stop()
        {
            running = false;
            if (client != null)
                client.Close();
        }

        private static async void Run()
        {
            client = new UdpClient(Port, AddressFamily.InterNetwork);
            IPEndPoint endPoint = new IPEndPoint(0, 0);

            byte[] sendData = Authenticator.GetBoxedCertificate();
            string base64 = Convert.ToBase64String(sendData);
            Logger.Info($"certif generated length= {base64.Length}: {base64.Substring(0,3)} ... {base64.Substring(base64.Length-4)}");
            sendData = Encoding.ASCII.GetBytes(base64);
            while (running)
            {
                try
                {
                    var receivedData = await client.ReceiveAsync();
                    byte[] data = receivedData.Buffer;
                    endPoint = receivedData.RemoteEndPoint;
                    string request = Encoding.UTF8.GetString(data);
                    Console.WriteLine($"Received Request from {endPoint} = {request}");
                    if (request == Request)
                    {
                        await client.SendAsync(sendData, sendData.Length, endPoint);
                        Console.WriteLine($"Certificate length = {sendData.Length} sent to {endPoint}");
                    } 
                }
                catch (Exception e)
                {
                    if (running)
                        Console.WriteLine("Error while running certserver. Restarting. Exception: " + e.Message);
                }
            }
        }
    }
}
