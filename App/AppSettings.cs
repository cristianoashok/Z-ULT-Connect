// AppSettings.cs - the user's choices, saved to %LOCALAPPDATA%\Z ULT Connect\settings.txt
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ZUltConnect.App
{
    internal static class AppSettings
    {
        private static readonly string FilePath = Path.Combine(AppLog.Folder, "settings.txt");
        private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        /// <summary>Close the panel when you click anywhere outside it.</summary>
        public static bool CloseOnClickOutside
        {
            get => Get("CloseOnClickOutside", "true") == "true";
            set => Set("CloseOnClickOutside", value ? "true" : "false");
        }

        /// <summary>Which companion wears the headphones: golden, cat, wolf or none.</summary>
        public static string Companion
        {
            get => Get("Companion", "golden");
            set => Set("Companion", value);
        }

        /// <summary>Last EQ preset seen, so the pill can show it before the headphones connect.</summary>
        public static int LastPreset
        {
            get => int.TryParse(Get("LastPreset", "-1"), out var v) ? v : -1;
            set { if (value != LastPreset) Set("LastPreset", value.ToString()); }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "Z ULT Connect";

        /// <summary>Start quietly in the tray when you log in to Windows.</summary>
        public static bool StartWithWindows
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(RunName) is string;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
                    else key.DeleteValue(RunName, false);
                }
                catch { }
            }
        }

        private static string Get(string key, string fallback)
        {
            Load();
            return Values.TryGetValue(key, out var v) ? v : fallback;
        }

        private static void Set(string key, string value)
        {
            Load();
            Values[key] = value;
            try
            {
                Directory.CreateDirectory(AppLog.Folder);
                var lines = new List<string>();
                foreach (var kv in Values) lines.Add($"{kv.Key}={kv.Value}");
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) Values[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
            }
            catch { }
        }
    }
}
