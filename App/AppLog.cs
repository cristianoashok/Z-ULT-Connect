// AppLog.cs - one shared log. Kept in memory for the log window and saved to
// %LOCALAPPDATA%\Z ULT Connect\log.txt so it can be sent for troubleshooting.
using System;
using System.Collections.Generic;
using System.IO;

namespace ZUltConnect.App
{
    internal static class AppLog
    {
        private static readonly object Gate = new();
        private static readonly List<string> Lines = new();

        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Z ULT Connect");
        public static string FilePath => Path.Combine(Folder, "log.txt");

        /// <summary>Raised on whatever thread added the line.</summary>
        public static event Action<string>? LineAdded;

        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, $"Z ULT Connect log - {DateTime.Now}{Environment.NewLine}");
            }
            catch { }
        }

        public static void Add(string line)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
            lock (Gate)
            {
                Lines.Add(stamped);
                if (Lines.Count > 5000) Lines.RemoveRange(0, 1000);
                try { File.AppendAllText(FilePath, stamped + Environment.NewLine); } catch { }
            }
            LineAdded?.Invoke(stamped);
        }

        public static string[] Snapshot()
        {
            lock (Gate) return Lines.ToArray();
        }
    }
}
