﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace iShare
{
    public static class Utils
    {
        public static string GetDataDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iShare");
        }

        public static string GetCertDir()
        {
            return Path.Combine(GetDataDir(), "cert");
        }

        public static string GetHostname()
        {
            return Environment.MachineName;
        }

        public static IPAddress GetLocalIPAddress()
        {
            var ip = Makaretu.Dns.MulticastService.GetNetworkInterfaces().FirstOrDefault((i) =>
                String.IsNullOrEmpty(Server.current.SelectedInterface) || i.Id == Server.current.SelectedInterface)?.GetIPProperties().UnicastAddresses
                .FirstOrDefault((a) => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && a.Address.GetAddressBytes().Length == 4)?.Address
                ?? IPAddress.Loopback;
            Console.WriteLine("Got ip " + ip?.ToString());
            return ip;
        }

        public static string BytesToHumanReadable(long _bytes)
        {
            double bytes = _bytes;
            string[] suffixes = { "B", "kB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes > 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return String.Format("{0:0.00} {1}", bytes, suffixes[order]);
        }

        public static string SanitizePath(string name)
        {
            string invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(Path.GetInvalidPathChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
        }

        private const string DownloadsGUID = "{374DE290-123F-4565-9164-39C4925E467B}";
        public static string GetDefaultDownloadFolder()
        {

            string path = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.Personal));
            //path = Path.Combine(path, "Downloads");
            Console.WriteLine("Default Path = " + path);
            return path;
            //int result = Shell32.SHGetKnownFolderPath(new Guid(DownloadsGUID),
            //(uint)0x00004000 /*Don't verify*/, new IntPtr(0), out IntPtr outPath);
            //if (result >= 0)
            //{
            //    string path = Marshal.PtrToStringUni(outPath);
            //    Marshal.FreeCoTaskMem(outPath);
            //    return path;
            //}
            //else
            //{
            //    throw new ExternalException("Unable to retrieve the known folder path. It may not "
            //        + "be available on this system.", result);
            //}
        }

        public static Icon GetFileIcon(string name, bool largeIcon)
        {
            var shfi = new Shell32.Shfileinfo();
            var flags = Shell32.ShgfiIcon | Shell32.ShgfiUsefileattributes;
            if (!largeIcon)
                flags += Shell32.ShgfiSmallicon;
            else
                flags += Shell32.ShgfiLargeicon;
            Shell32.SHGetFileInfo(name, Shell32.FileAttributeNormal, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
            var icon = (Icon)Icon.FromHandle(shfi.hIcon).Clone();
            User32.DestroyIcon(shfi.hIcon);
            return icon;
        }

        public static string GetUserPicturePath()
        {
            var sb = new StringBuilder(1000);
            Shell32.GetUserTilePath(Environment.UserName, 0x80000000, sb, sb.Capacity);
            return sb.ToString();
        }

        static class Shell32
        {
            private const int MaxPath = 256;
            [StructLayout(LayoutKind.Sequential)]
            public struct Shfileinfo
            {
                private const int Namesize = 80;
                public readonly IntPtr hIcon;
                private readonly int iIcon;
                private readonly uint dwAttributes;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
                private readonly string szDisplayName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Namesize)]
                private readonly string szTypeName;
            };
            public const uint ShgfiIcon = 0x000000100;
            public const uint ShgfiLargeicon = 0x000000000;
            public const uint ShgfiSmallicon = 0x000000001;
            public const uint ShgfiUsefileattributes = 0x000000010;
            public const uint FileAttributeNormal = 0x00000080;
            [DllImport("Shell32.dll")]
            public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref Shfileinfo psfi, uint cbFileInfo, uint uFlags);
            [DllImport("Shell32.dll")]
            public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
            [DllImport("shell32.dll", EntryPoint = "#261", CharSet = CharSet.Unicode, PreserveSig = false)]
            public static extern void GetUserTilePath(string username, UInt32 whatever, StringBuilder picpath, int maxLength);
        }
        public static class User32
        {
            [DllImport("User32.dll")]
            public static extern int DestroyIcon(IntPtr hIcon);
        }
    }
}
