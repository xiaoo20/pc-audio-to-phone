using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PcAudioServer
{
    /// <summary>
    /// 手机侧的一键连接：找 adb、装/更新 APK、建 USB 隧道、拉起手机端 App，
    /// 并且**持续盯着 USB 状态** —— 线拔了再插回来会自动重建隧道并把 App 拉回来。
    ///
    /// 这些活以前全在 启动.bat 里，而且只在启动那一刻做一次：
    /// 所以 USB 一断，隧道就没了，App 退回 Wi-Fi 之后再也回不到 USB，
    /// 必须关掉整个软件重来。现在搬进 exe 里，并且是常驻轮询的。
    /// </summary>
    public sealed class PhoneLink
    {
        public const string PKG = "com.dsh.pcaudio";

        public event Action<string> Log;
        public event Action Changed;

        string adb;
        string lastUsbSerial, lastWifiSerial;
        string lastAppliedKey;          // serial|host|port，用来判断"要不要重新拉起 App"
        string tunnelSig;               // 当前 reverse 隧道签名
        bool installedHashOk;
        DateTime lastApkCheck = DateTime.MinValue;
        DateTime lastAdbWarn = DateTime.MinValue;

        public bool AdbFound { get { return !string.IsNullOrEmpty(adb); } }
        public string UsbSerial { get; private set; }
        public string WifiSerial { get; private set; }
        public bool TunnelOk { get; private set; }
        public string Transport { get; private set; }   // "USB 隧道" / "Wi-Fi" / "未连接"
        public string LastMessage { get; private set; }

        void Emit(string s) { var h = Log; if (h != null) { try { h(s); } catch { } } }
        void Bump() { var h = Changed; if (h != null) { try { h(); } catch { } } }

        // ================= adb 定位 =================

        public void Init(string baseDir)
        {
            adb = FindAdb(baseDir);
            if (adb == null)
            {
                Emit("[手机] 没找到 adb.exe —— 把 adb 文件夹放到程序旁边，或让 adb 在 PATH 里");
                LastMessage = "未找到 adb";
            }
            else
            {
                Emit("[手机] adb: " + adb);
                TryRun("start-server", 8000);
            }
            Bump();
        }

        static string FindAdb(string baseDir)
        {
            var cands = new List<string>();
            try
            {
                // 程序旁边 / 上一层（发布包里 exe 在 bin\ 下，adb 在根目录的 adb\ 里）
                string[] roots = { baseDir, Path.Combine(baseDir, ".."), Path.Combine(baseDir, "..", "..") };
                foreach (var r in roots)
                {
                    cands.Add(Path.Combine(r, "adb", "adb.exe"));
                    cands.Add(Path.Combine(r, "adb.exe"));
                }
                cands.Add(@"D:\scrcpy-win64-v3.3.3\adb.exe");
                cands.Add(@"D:\Unity\2023.1.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe");
            }
            catch { }
            foreach (var c in cands)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            // PATH
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var d in path.Split(';'))
                {
                    if (d.Trim().Length == 0) continue;
                    try
                    {
                        string p = Path.Combine(d.Trim(), "adb.exe");
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ================= 跑 adb =================

        (int code, string output) Run(string args, int timeoutMs)
        {
            if (adb == null) return (-1, "");
            try
            {
                var psi = new ProcessStartInfo(adb, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, outp); }
                    return (p.ExitCode, outp + err);
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        void TryRun(string args, int timeoutMs) { try { Run(args, timeoutMs); } catch { } }

        // ================= 设备枚举 =================

        void RefreshDevices()
        {
            UsbSerial = null;
            WifiSerial = null;
            var r = Run("devices -l", 8000);
            if (r.code != 0) return;
            foreach (var raw in r.output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("List of devices")) continue;
                if (line.StartsWith("*")) continue;
                if (line.IndexOf("device ", StringComparison.Ordinal) < 0 &&
                    !line.EndsWith("device")) continue;
                if (line.StartsWith("adb") || line.StartsWith("daemon")) continue;

                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp <= 0) continue;
                string serial = line.Substring(0, sp);

                bool isWifi = serial.Contains(":");
                if (isWifi)
                {
                    if (WifiSerial == null) WifiSerial = serial;
                }
                else
                {
                    // USB：型号里带 "usb:" 或者压根没有冒号端口的都算
                    if (UsbSerial == null) UsbSerial = serial;
                }
            }
        }

        static string LocalIp()
        {
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        // ================= 主循环（由 UI 的定时器在后台线程调用） =================

        public void Tick(int port, string apkPath)
        {
            if (adb == null) return;
            try
            {
                RefreshDevices();

                // ---- 1. 有 USB 设备：确保隧道在，必要时重建 + 重新拉起 App ----
                if (UsbSerial != null)
                {
                    string want = "UsbFfs " + port;
                    string serial = UsbSerial;

                    if (lastUsbSerial != serial)
                    {
                        Emit("[手机] 检测到 USB 设备 " + serial);
                        tunnelSig = null;      // 换了设备，隧道要重来
                    }

                    // 隧道在不在？不在就建
                    if (!TunnelPresent(port, serial))
                    {
                        TunnelOk = false;
                        TryRun("-s " + serial + " reverse --remove-all", 8000);
                        var rr = Run("-s " + serial + " reverse tcp:" + port + " tcp:" + port, 10000);
                        TunnelOk = (rr.code == 0) && TunnelPresent(port, serial);
                        if (TunnelOk)
                            Emit("[手机] 已建立 USB 隧道 adb reverse tcp:" + port);
                        else
                            Emit("[手机] USB 隧道建立失败，本次会走 Wi-Fi（重新插拔数据线可修复）");
                        tunnelSig = TunnelOk ? ("usb:" + serial) : null;
                        lastAppliedKey = null;     // 隧道变了，App 要重新拉一次
                    }
                    else if (!TunnelOk)
                    {
                        TunnelOk = true;
                        tunnelSig = "usb:" + serial;
                        lastAppliedKey = null;
                    }

                    if (TunnelOk)
                    {
                        EnsureApk(serial, apkPath);
                        ApplyToPhone(serial, "127.0.0.1", port, "USB 隧道");
                        Transport = "USB 隧道";
                        lastUsbSerial = serial;
                        Bump();
                        return;
                    }

                    Transport = "USB（隧道不可用）";
                    lastUsbSerial = serial;
                    Bump();
                    // 隧道建不起来就退回去试 Wi-Fi（如果 adb 里也有 Wi-Fi 设备）
                }

                // ---- 2. 没有 USB：看有没有 Wi-Fi adb 设备 ----
                if (WifiSerial != null)
                {
                    EnsureApk(WifiSerial, apkPath);
                    ApplyToPhone(WifiSerial, LocalIp(), port, "Wi-Fi");
                    Transport = "Wi-Fi";
                    lastWifiSerial = WifiSerial;
                    Bump();
                    return;
                }

                // ---- 3. 什么都没有 ----
                if (UsbSerial == null && WifiSerial == null)
                {
                    if (lastUsbSerial != null || lastWifiSerial != null)
                        Emit("[手机] 设备已断开");
                    lastUsbSerial = null;
                    lastWifiSerial = null;
                    lastAppliedKey = null;
                    TunnelOk = false;
                    Transport = "未连接";
                    Bump();
                }
            }
            catch (Exception ex)
            {
                if ((DateTime.Now - lastAdbWarn).TotalSeconds > 20)
                {
                    lastAdbWarn = DateTime.Now;
                    Emit("[手机] 轮询出错: " + ex.Message);
                }
            }
        }

        bool TunnelPresent(int port, string serial)
        {
            var r = Run("-s " + serial + " reverse --list", 8000);
            if (r.code != 0) return false;
            return r.output.IndexOf("tcp:" + port, StringComparison.Ordinal) >= 0;
        }

        /// <summary>把 App 拉起来（只有在目标发生变化时才真的动它，免得每次轮询都重启）。</summary>
        void ApplyToPhone(string serial, string host, int port, string how)
        {
            string key = serial + "|" + host + "|" + port;
            if (key == lastAppliedKey) return;

            // 特意不传 --ei buffer：让手机端用它自己记住的缓冲值，
            // 否则每次拉起都会被这里覆盖掉，用户在手机上选的挡位就白设了。
            Emit("[手机] 拉起 App（" + how + "，host=" + host + ":" + port + "）");
            TryRun("-s " + serial + " shell am force-stop " + PKG, 10000);
            TryRun("-s " + serial + " shell am start -n " + PKG + "/.MainActivity"
                   + " --es host " + host + " --ei port " + port, 10000);
            lastAppliedKey = key;
        }

        /// <summary>判断 APK 有没有变过，变了就重装。返回 true 表示装了。</summary>
        bool EnsureApk(string serial, string apkPath)
        {
            if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath)) return false;
            if ((DateTime.Now - lastApkCheck).TotalSeconds < 5) return false;
            lastApkCheck = DateTime.Now;

            string hash;
            try
            {
                using (var md5 = MD5.Create())
                using (var fs = File.OpenRead(apkPath))
                    hash = BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
            }
            catch { return false; }

            string marker = Path.Combine(Path.GetDirectoryName(apkPath), "apk_installed.txt");
            string old = null;
            try { if (File.Exists(marker)) old = File.ReadAllText(marker).Trim(); } catch { }

            bool present = Run("-s " + serial + " shell pm path " + PKG, 8000).output.Contains("package:");
            if (present && string.Equals(old, hash, StringComparison.OrdinalIgnoreCase))
            {
                installedHashOk = true;
                return false;
            }

            Emit("[手机] 正在安装手机端 App（首次或已更新）...");
            var r = Run("-s " + serial + " install -r --no-incremental \"" + apkPath + "\"", 180000);
            if (r.output.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { File.WriteAllText(marker, hash); } catch { }
                Emit("[手机] App 安装完成");
                installedHashOk = true;
                return true;
            }
            Emit("[手机] 安装失败（手机锁屏 / 没开「通过 USB 安装应用」 / 被 GKD 点掉弹窗）");
            return false;
        }

        /// <summary>用户点「重新连接」时用：清掉状态，强制重来一遍。</summary>
        public void ForceReconnect()
        {
            lastAppliedKey = null;
            tunnelSig = null;
            TryRun("kill-server", 5000);
            TryRun("start-server", 8000);
        }
    }
}
