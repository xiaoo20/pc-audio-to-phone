using System;
using System.Windows.Forms;

namespace PcAudioServer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            int port = ArgI(args, 0, AudioServer.DEFAULT_PORT);
            string codec = (args.Length > 1 ? args[1] : "opus").ToLowerInvariant();
            int rate = ArgI(args, 2, 48000);
            int channels = ArgI(args, 3, 2);
            int bitrate = ArgI(args, 4, 128);
            bool minimized = Has(args, "--minimized");

            WinTimer.Begin();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(port, codec, rate, channels, bitrate, minimized));
            }
            finally { WinTimer.End(); }
        }

        static bool Has(string[] a, string flag)
        {
            if (a == null) return false;
            foreach (var s in a) if (string.Equals(s, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static int ArgI(string[] a, int i, int def)
        {
            if (a == null || a.Length <= i) return def;
            int v;
            return int.TryParse(a[i], out v) ? v : def;
        }
    }
}
