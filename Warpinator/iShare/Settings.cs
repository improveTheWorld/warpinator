using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iShare
{
    public class Settings
    {
        public string DownloadDir = "";
        public string UUID = "";
        public string NetworkInterface = "";      
        public int Port = 4200;       
        public string GroupCode = "warpinator";
        public bool AllowOverwrite = false;
        public bool AutoAccept = false;
        public bool RunInBackground = false;
        public bool NotifyIncoming = true;
        public bool CheckForUpdates = false;
        public bool FirstRun = true;
    }
}
