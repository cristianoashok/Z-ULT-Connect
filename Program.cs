// Program.cs - where Z ULT Connect starts.
//   --demo   use a simulated ULT Wear (no headphones needed)
//   --tray   start hidden in the tray (used by "Start with Windows")
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ZUltConnect.App;

namespace ZUltConnect
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Only one copy at a time (two copies would fight over the headphones).
            using var mutex = new Mutex(true, @"Local\ZUltConnect.SingleInstance", out bool firstCopy);
            if (!firstCopy)
            {
                MessageBox.Show("Z ULT Connect is already running.\n\nLook for the headphone icon by the clock " +
                                "(it may be hidden under the ^ arrow).", "Z ULT Connect",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            AppLog.Start();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => SaveCrash(e.ExceptionObject as Exception);
            Application.ThreadException += (_, e) =>
            {
                SaveCrash(e.Exception);
                AppLog.Add("• Error: " + e.Exception.Message);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
            bool startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            Application.Run(new TrayApp(demo, startHidden));
        }

        private static void SaveCrash(Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(AppLog.Folder);
                File.AppendAllText(Path.Combine(AppLog.Folder, "crash.txt"),
                                   $"{DateTime.Now}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
