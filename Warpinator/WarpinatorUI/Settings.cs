namespace Warpinator
{
    namespace Properties
    {
        internal sealed partial class Settings 
        {
            public iShare.Settings getServerSettings()
            {
                return new iShare.Settings
                {


                    AllowOverwrite = AllowOverwrite,
                    AutoAccept = AutoAccept,
                    CheckForUpdates = CheckForUpdates,
                    DownloadDir = DownloadDir,
                    FirstRun = FirstRun,
                    GroupCode = GroupCode,
                    NetworkInterface = NetworkInterface,
                    NotifyIncoming = NotifyIncoming,
                    Port = Port,
                    RunInBackground = RunInBackground,
                    UUID = UUID
                };
            }
        }   
    }
}
