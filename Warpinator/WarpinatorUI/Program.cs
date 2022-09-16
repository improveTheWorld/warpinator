using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace Warpinator
{
    static class Program
    {
        //internal static ILoggerFactoryAdapter Log { get; private set; }
        internal static List<string> SendPaths = new List<string>(); 
        static NamedPipeServerStream pipeServer;

        [STAThread]
        static void Main(string[] args)
        {

            Console.WriteLine("Starting warpinator!");

            var mutex = new Mutex(true, "warpinator", out bool created);
            // Process arguments
            if (args.Length > 0)
            {
                foreach (var path in args)
                {
                    Console.WriteLine("Got path to send: " + path);
                    if (File.Exists(path) || Directory.Exists(path))
                        SendPaths.Add(path);
                    else Console.WriteLine("Path does not exist");
                }
                if (!created)
                {
                    Console.WriteLine("Passing paths to main process...");
                    using (var pipeClient = new NamedPipeClientStream(".", "warpsendto", PipeDirection.Out))
                    {
                        pipeClient.Connect();
                        using (var sw = new StreamWriter(pipeClient))
                        {
                            sw.AutoFlush = true;
                            foreach (var path in SendPaths)
                                sw.WriteLine(path);
                        }
                    }
                }   
            }
            // Run application if not yet running
            if (created)
            {
                Console.WriteLine("Starting application...");
                Task.Run(RunPipeServer);
                
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());

                pipeServer.Close();
            }
            mutex.Dispose();
            Console.WriteLine("Exit");
        }

        static void RunPipeServer()
        {
            pipeServer = new NamedPipeServerStream("warpsendto", PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances);
            try
            {
                using (var sr = new StreamReader(pipeServer))
                {
                    while (true)
                    {
                        pipeServer.WaitForConnection();
                        while (pipeServer.IsConnected)
                        {
                            var path = sr.ReadLine();
                            if (!(File.Exists(path) || Directory.Exists(path)))
                                continue;
                            SendPaths.Add(path);
                            Console.WriteLine($"Got path {path}");
                        }
                        Form1.OnSendTo();
                        pipeServer.Disconnect();
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Pipe server quit ({e.GetType()})");
            }
        }
    }
}
