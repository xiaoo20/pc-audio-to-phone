using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

namespace PcAudioServer
{
    /// 会话存活标志（显式 volatile，跨线程可见）。
    sealed class Live { public volatile bool V = true; }

    /// 手机端在运行中改的码率。
    class BitrateBox { public volatile int Value; }

    /// <summary>音频推流引擎。可以随时 Start / Stop，UI 直接调。</summary>
    public sealed class AudioServer
    {
        public const int DEFAULT_PORT = 28700;
        public const int BEACON_PORT = 28701;
        const int MAGIC = 0x32414350;      // "PCA2"
        const int FRAME_MS = 10;           // 每帧固定 10ms 音频
        const int QUEUE_FRAMES = 12;       // 发送队列深度（12 帧 = 最多 120ms 排空余量）

        // ---- 对外事件 ----
        public event Action<string> Log;
        public event Action StateChanged;

        // ---- 启动参数 ----
        public int Port { get; private set; }
        public string StartCodec { get; private set; }
        public int StartRate { get; private set; }
        public int StartChannels { get; private set; }
        public int StartBitrate { get; private set; }

        // ---- 运行状态 ----
        volatile bool running;
        public bool IsRunning { get { return running; } }
        int clients;
        public int ClientCount { get { return Volatile.Read(ref clients); } }
        public volatile string CurrentInfo = "";

        // ---- 手机端记住的偏好（改完在下一次连接生效，和原版行为一致）----
        volatile int prefChannels = 0;
        volatile int prefBitrateKbps = 0;
        volatile int prefCodec = -1;       // -1 = 用启动参数, 0 = PCM, 1 = Opus
        volatile int prefPcmRate = 0;      // 0 = 用启动参数
        volatile int sessionGen = 0;

        TcpListener listener;
        Thread acceptThread, beaconThread;

        public int PrefCodec { get { return prefCodec; } }
        public int PrefBitrateKbps { get { return prefBitrateKbps; } }
        public int PrefPcmRate { get { return prefPcmRate; } }

        void Emit(string s) { var h = Log; if (h != null) { try { h(s); } catch { } } }
        void Bump() { var h = StateChanged; if (h != null) { try { h(); } catch { } } }

        // ================= 启停 =================

        public bool Start(int port, string codec, int rate, int channels, int bitrate)
        {
            if (running) return true;
            Port = (port < 1 || port > 65535) ? DEFAULT_PORT : port;
            StartCodec = (codec == "pcm") ? "pcm" : "opus";
            StartRate = (rate < 8000 || rate > 192000) ? 48000 : rate;
            StartChannels = (channels < 1 || channels > 2) ? 2 : channels;
            StartBitrate = bitrate < 8 ? 8 : bitrate;

            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Emit("[错误] 端口 " + Port + " 监听失败: " + ex.Message);
                return false;
            }

            running = true;
            sessionGen = 0;
            clients = 0;

            Emit("========================================");
            Emit("  电脑声音 -> 手机   服务端");
            Emit("========================================");
            Emit("  监听 : 0.0.0.0:" + Port + "  (USB 与 Wi-Fi 都可用)");
            Emit("  默认 : " + (StartCodec == "opus"
                    ? ("Opus @" + StartBitrate + " kbps (48kHz)")
                    : ("无损 PCM " + StartRate + "Hz / " + StartChannels + "ch")));
            Emit("  发现 : UDP 广播端口 " + BEACON_PORT);
            Emit("  等待手机连接 ...");
            Emit("");

            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "accept";
            acceptThread.Start();

            beaconThread = new Thread(BeaconLoop);
            beaconThread.IsBackground = true;
            beaconThread.Name = "beacon";
            beaconThread.Start();

            Bump();
            return true;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            Interlocked.Increment(ref sessionGen);   // 让所有会话立刻收摊
            Emit("[服务] 已停止");
            Bump();
        }

        // 手机端在运行时改偏好。改完由手机主动重连生效。
        public void SetRemoteCodec(int codecId)
        {
            if (codecId != 0 && codecId != 1) return;
            prefCodec = codecId;
            Emit("[控制] 手机端要求改用 " + (codecId == 1 ? "Opus" : "无损 PCM") + "（重连后生效）");
            Bump();
        }

        public void SetRemoteBitrate(int kbps)
        {
            if (kbps < 8) kbps = 8;
            if (kbps > 510) kbps = 510;
            prefBitrateKbps = kbps;
            Emit("[控制] 手机端把码率改为 " + kbps + " kbps");
            Bump();
        }

        public void SetRemoteChannels(int ch)
        {
            if (ch != 1 && ch != 2) return;
            prefChannels = ch;
            Emit("[控制] 手机端要求 " + ch + " 声道（下次连接生效）");
            Bump();
        }

        public void SetRemotePcmRate(int rate)
        {
            if (rate != 44100 && rate != 48000) return;
            prefPcmRate = rate;
            Emit("[控制] 手机端要求无损采样率 " + rate + "Hz（重连后生效）");
            Bump();
        }

        /// <summary>
        /// 清掉手机端留下的临时覆盖，回到"以桌面端设置为准"。
        /// 桌面端一改参数就得调用它 —— 否则手机上改过的东西会一直压着，
        /// 桌面端改了半天不生效，两边设置就永远对不上。
        /// </summary>
        public void ClearRemotePrefs()
        {
            prefCodec = -1;
            prefPcmRate = 0;
            prefChannels = 0;
            prefBitrateKbps = 0;
            Bump();
        }

        // ================= 接受连接 =================

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }

                TcpClient c = client;
                int myGen = Interlocked.Increment(ref sessionGen);
                Thread th = new Thread(() =>
                {
                    Interlocked.Increment(ref clients);
                    Bump();
                    Emit("[连接] 来自 " + c.Client.RemoteEndPoint);
                    try { StreamTo(c, myGen); }
                    catch (Exception ex) { Emit("[断开] " + ex.Message); }
                    finally
                    {
                        try { c.Close(); } catch { }
                        Interlocked.Decrement(ref clients);
                        CurrentInfo = "";
                        Emit("[连接] 已结束");
                        Emit("");
                        Bump();
                    }
                });
                th.IsBackground = true;
                th.Name = "session";
                th.Start();
            }
        }

        // ================= 单路推流 =================

        void StreamTo(TcpClient client, int myGen)
        {
            client.NoDelay = true;
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                byte[] ka = new byte[12];
                BitConverter.GetBytes(1).CopyTo(ka, 0);
                BitConverter.GetBytes(5000).CopyTo(ka, 4);
                BitConverter.GetBytes(2000).CopyTo(ka, 8);
                client.Client.IOControl(IOControlCode.KeepAliveValues, ka, null);
            }
            catch { }
            NetworkStream net = client.GetStream();

            // 本会话的编码配置：手机改过就听手机的，否则用启动参数
            bool useOpus = (prefCodec >= 0) ? (prefCodec == 1) : (StartCodec == "opus");
            int bitrate = prefBitrateKbps > 0 ? prefBitrateKbps : StartBitrate;

            WasapiLoopbackCapture capture = new WasapiLoopbackCapture();
            WaveFormat src = capture.WaveFormat;
            bool srcFloat = (src.Encoding == WaveFormatEncoding.IeeeFloat && src.BitsPerSample == 32);
            bool srcPcm16 = (src.Encoding == WaveFormatEncoding.Pcm && src.BitsPerSample == 16);
            Emit("[音频] 采集源 " + src.SampleRate + "Hz / " + src.Channels + "ch / "
                 + src.BitsPerSample + "bit / " + src.Encoding);

            int outRate, outCh;
            if (useOpus)
            {
                outRate = 48000;   // Opus 解码只能出 48k，安卓解码器也要求 48000
                outCh = prefChannels > 0 ? prefChannels : (src.Channels > 2 ? 2 : src.Channels);
            }
            else
            {
                outRate = prefPcmRate > 0 ? prefPcmRate : StartRate;
                outCh = prefChannels > 0 ? prefChannels : StartChannels;
                if (outCh > src.Channels) outCh = src.Channels;
            }
            if (outCh < 1) outCh = 1;

            // 握手头（20 字节，小端）
            byte[] hdr = new byte[20];
            WriteI32(hdr, 0, MAGIC);
            WriteI32(hdr, 4, useOpus ? 1 : 0);
            WriteI32(hdr, 8, outRate);
            WriteI16(hdr, 12, (ushort)outCh);
            WriteI16(hdr, 14, 16);
            WriteI32(hdr, 16, useOpus ? bitrate : 0);
            net.Write(hdr, 0, hdr.Length);
            net.Flush();

            int frameSamples = outRate / 1000 * FRAME_MS;   // 每帧单声道样本数
            int frameLen = frameSamples * outCh;            // 交错样本数

            Resampler rs = null;
            if (src.SampleRate != outRate || src.Channels != outCh)
            {
                rs = new Resampler(src.SampleRate, src.Channels, outRate, outCh);
                Emit("[重采样] " + src.SampleRate + "Hz/" + src.Channels + "ch  ->  "
                     + outRate + "Hz/" + outCh + "ch");
            }

            OpusEncoder enc = null;
            if (useOpus)
            {
                enc = new OpusEncoder(outRate, outCh, OpusApplication.OPUS_APPLICATION_AUDIO);
                enc.Bitrate = bitrate * 1000;
                enc.Complexity = 10;          // 默认 5，10 是纯质量提升、CPU 开销可忽略
                enc.UseVBR = false;
                Emit("[编码] Opus " + outRate + "Hz / " + outCh + "ch / " + bitrate
                     + " kbps / complexity 10 / 每帧 " + FRAME_MS + "ms");
            }
            else
            {
                Emit("[编码] 无损 PCM " + outRate + "Hz / " + outCh + "ch / 16bit  ("
                     + ((long)outRate * outCh * 16 / 1000) + " kbps)");
            }

            CurrentInfo = (useOpus ? ("Opus " + bitrate + "k") : ("无损 PCM " + outRate + "Hz"))
                          + " · " + outRate + "Hz/" + outCh + "ch";
            Bump();

            BitrateBox bitrateBox = new BitrateBox();
            bitrateBox.Value = bitrate;
            int appliedBitrate = bitrate;

            Live live = new Live();
            BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(QUEUE_FRAMES);
            // 抖动缓冲：WASAPI 环回是一次给 50~70ms（一阵一阵的），
            // 采集侧只负责切帧+编码，由节拍器按墙钟一帧一帧均匀发出，
            // 手机收到的就是平滑的 10ms 节奏，不用为了扛突发把缓冲开很大。
            ConcurrentQueue<byte[]> pending = new ConcurrentQueue<byte[]>();
            const int PENDING_MAX = 20;

            int dropped = 0;
            long droppedLate = 0, sent = 0, emitted = 0, silenceFrames = 0, pacerTicks = 0, cbCount = 0;

            Thread writer = new Thread(() =>
            {
                try
                {
                    foreach (byte[] chunk in queue.GetConsumingEnumerable())
                    {
                        net.Write(chunk, 0, chunk.Length);
                        Interlocked.Add(ref sent, chunk.Length);
                    }
                }
                catch (Exception wex) { Emit("[连接] 写失败: " + wex.Message); live.V = false; }
            });
            writer.IsBackground = true;
            writer.Start();

            float[] fbuf = new float[1 << 16];
            short[] sbuf = new short[1 << 15];
            short[] frameBuf = new short[frameLen];
            int frameFill = 0;
            byte[] opusOut = new byte[8192];

            Action<short[], int> emit = (samples, count) =>
            {
                int off = 0;
                while (off < count)
                {
                    int take = frameLen - frameFill;
                    if (take > count - off) take = count - off;
                    Array.Copy(samples, off, frameBuf, frameFill, take);
                    frameFill += take;
                    off += take;

                    if (frameFill == frameLen)
                    {
                        if (useOpus)
                        {
                            int pb = bitrateBox.Value;
                            if (pb != appliedBitrate)
                            {
                                try { enc.Bitrate = pb * 1000; }
                                catch (Exception bex) { Emit("[控制] 改码率失败: " + bex.Message); }
                                appliedBitrate = pb;
                            }
                            int n;
                            lock (enc) { n = enc.Encode(frameBuf, 0, frameSamples, opusOut, 0, opusOut.Length); }
                            if (n > 0)
                            {
                                byte[] chunk = new byte[n + 2];
                                chunk[0] = (byte)(n & 0xFF);
                                chunk[1] = (byte)((n >> 8) & 0xFF);
                                Buffer.BlockCopy(opusOut, 0, chunk, 2, n);
                                pending.Enqueue(chunk);
                            }
                        }
                        else
                        {
                            byte[] chunk = new byte[frameLen * 2];
                            Buffer.BlockCopy(frameBuf, 0, chunk, 0, chunk.Length);
                            pending.Enqueue(chunk);
                        }
                        frameFill = 0;
                    }
                }
            };

            capture.DataAvailable += (s, a) =>
            {
                if (!live.V) return;
                Interlocked.Increment(ref cbCount);
                try
                {
                    int frames;
                    if (srcFloat)
                    {
                        int floats = a.BytesRecorded / 4;
                        if (floats > fbuf.Length) fbuf = new float[floats];
                        for (int i = 0; i < floats; i++) fbuf[i] = BitConverter.ToSingle(a.Buffer, i * 4);
                        frames = floats / src.Channels;
                    }
                    else if (srcPcm16)
                    {
                        int samples = a.BytesRecorded / 2;
                        if (samples > fbuf.Length) fbuf = new float[samples];
                        for (int i = 0; i < samples; i++)
                            fbuf[i] = BitConverter.ToInt16(a.Buffer, i * 2) / 32768f;
                        frames = samples / src.Channels;
                    }
                    else return;

                    if (frames <= 0) return;

                    int n;
                    if (rs == null)
                    {
                        int total = frames * src.Channels;
                        if (total > sbuf.Length) sbuf = new short[total];
                        for (int i = 0; i < total; i++)
                        {
                            float f = fbuf[i];
                            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                            sbuf[i] = (short)(f * 32767f);
                        }
                        n = total;
                    }
                    else
                    {
                        n = rs.Process(fbuf, frames, sbuf);
                    }

                    if (n > 0) emit(sbuf, n);
                }
                catch (Exception dex)
                {
                    Emit("[音频] 回调异常: " + dex.GetType().Name + ": " + dex.Message);
                    live.V = false;
                }
            };

            capture.RecordingStopped += (s, a) =>
            {
                if (a != null && a.Exception != null) Emit("[音频] 采集中止: " + a.Exception.Message);
                live.V = false;
            };

            capture.StartRecording();
            Emit("[音频] 开始推送 ...");
            Emit("");

            // 反向控制通道：手机 -> 电脑
            //   0x01 u16 kbps   改码率
            //   0x02 u16 ch     改声道
            //   0x03 u16 codec  改编码（0=PCM, 1=Opus）
            //   0x04 u16 rate   改无损采样率（44100/48000）
            Thread cmdThread = new Thread(() =>
            {
                byte[] cmd = new byte[3];
                try
                {
                    while (live.V)
                    {
                        int coff = 0;
                        while (coff < 3)
                        {
                            int r = net.Read(cmd, coff, 3 - coff);
                            if (r <= 0) { live.V = false; return; }
                            coff += r;
                        }
                        int val = (cmd[1] & 0xFF) | ((cmd[2] & 0xFF) << 8);
                        if (cmd[0] == 0x01)
                        {
                            SetRemoteBitrate(val);
                            bitrateBox.Value = prefBitrateKbps;
                        }
                        else if (cmd[0] == 0x02) SetRemoteChannels(val);
                        else if (cmd[0] == 0x03) SetRemoteCodec(val);
                        else if (cmd[0] == 0x04) SetRemotePcmRate(val);
                    }
                }
                catch { live.V = false; }
            });
            cmdThread.IsBackground = true;
            cmdThread.Start();

            // ---------------------------------------------------------------
            // 实时节拍器
            // 旧版用 Thread.Sleep(3) 轮询，而 Windows 的 Sleep 最小粒度是 15.6ms
            // （这台机器上 timeBeginPeriod(1) 还失效），结果电脑静音时只能发出约
            // 60 帧/秒 —— 每帧 10ms 音频，等于整条流比实时慢 40%，手机 AudioTrack
            // 必然饿死：听感就是断续、听不到声音。现在改成高精度等待定时器 +
            // 绝对时钟：以"到此刻本该送出多少样本"为准，欠多少补多少，绝不超发。
            // ---------------------------------------------------------------
            Thread pacer = new Thread(() =>
            {
                IntPtr timer = HiResTimer.Create(FRAME_MS);
                short[] sil = new short[frameLen];
                byte[] silPcm = useOpus ? null : new byte[frameLen * 2];
                byte[] silOut = new byte[8192];
                Stopwatch clock = Stopwatch.StartNew();

                while (live.V)
                {
                    if (timer != IntPtr.Zero) HiResTimer.Wait(timer);
                    else Thread.Sleep(FRAME_MS);

                    Interlocked.Increment(ref pacerTicks);
                    long due = (long)(clock.Elapsed.TotalSeconds * outRate) * outCh;

                    int guard = 0;
                    while (live.V && guard++ < 8)
                    {
                        long have = Interlocked.Read(ref emitted);
                        if (have + frameLen > due) break;

                        while (pending.Count > PENDING_MAX)
                        {
                            byte[] junk;
                            if (!pending.TryDequeue(out junk)) break;
                            Interlocked.Increment(ref droppedLate);
                        }

                        byte[] ready;
                        if (!pending.TryDequeue(out ready)) ready = null;

                        if (ready != null)
                        {
                            Enqueue(queue, ready, ref dropped);
                        }
                        else
                        {
                            // 电脑当前没出声：WASAPI 环回此时一个字节都不产
                            // （实测静音时 BytesRecorded 恒为 0）。必须自己补静音，
                            // 否则手机收不到数据、读超时后就断线重连。
                            try
                            {
                                if (useOpus)
                                {
                                    int sn;
                                    lock (enc) { sn = enc.Encode(sil, 0, frameSamples, silOut, 0, silOut.Length); }
                                    if (sn > 0)
                                    {
                                        byte[] chunk = new byte[sn + 2];
                                        chunk[0] = (byte)(sn & 0xFF);
                                        chunk[1] = (byte)((sn >> 8) & 0xFF);
                                        Buffer.BlockCopy(silOut, 0, chunk, 2, sn);
                                        Enqueue(queue, chunk, ref dropped);
                                    }
                                }
                                else
                                {
                                    Enqueue(queue, silPcm, ref dropped);
                                }
                                Interlocked.Increment(ref silenceFrames);
                            }
                            catch { break; }
                        }
                        Interlocked.Add(ref emitted, frameLen);
                    }
                }
                HiResTimer.Close(timer);
            });
            pacer.IsBackground = true;
            pacer.Start();

            int tick = 0;
            while (live.V)
            {
                Thread.Sleep(200);

                // 代次变了 = 有更新的连接进来，本会话立刻收摊，
                // 否则同一个扬声器上会同时跑好几路 WASAPI 环回采集，互相拖垮。
                if (sessionGen != myGen)
                {
                    Emit("[连接] 有更新的连接接入，结束本会话");
                    live.V = false;
                    break;
                }

                if (++tick % 50 == 0)
                {
                    Emit("[状态] 已发 " + (Interlocked.Read(ref sent) / 1024) + " KB"
                         + " / 丢弃 " + dropped + " 帧"
                         + " / 采集回调 " + Interlocked.Read(ref cbCount) + " 次"
                         + " / 补静音 " + Interlocked.Read(ref silenceFrames) + " 帧"
                         + " / 缓冲 " + pending.Count + " 帧");
                }
            }

            Emit("[音频] 采集回调 " + Interlocked.Read(ref cbCount)
                 + " 次，发送 " + (Interlocked.Read(ref sent) / 1024) + " KB");
            try { capture.StopRecording(); } catch { }
            try { queue.CompleteAdding(); } catch { }
            try { writer.Join(1500); } catch { }
            try { capture.Dispose(); } catch { }

            Emit("[统计] 共发送 " + (Interlocked.Read(ref sent) / 1024) + " KB，丢弃 " + dropped + " 帧"
                 + "，缓冲溢出丢弃 " + Interlocked.Read(ref droppedLate) + " 帧"
                 + "，补静音 " + Interlocked.Read(ref silenceFrames) + " 帧"
                 + (useOpus ? ("，实际码率 " + appliedBitrate + " kbps") : ""));
        }

        static void Enqueue(BlockingCollection<byte[]> q, byte[] chunk, ref int dropped)
        {
            if (!q.TryAdd(chunk))
            {
                byte[] old;
                if (q.TryTake(out old)) dropped++;
                q.TryAdd(chunk);
            }
        }

        // ================= 局域网广播发现 =================

        void BeaconLoop()
        {
            byte[] payload = Encoding.ASCII.GetBytes("PCAUDIO1 " + Port);
            UdpClient udp = null;
            try { udp = new UdpClient(); udp.EnableBroadcast = true; }
            catch { return; }

            while (running)
            {
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (ua.IPv4Mask == null) continue;
                            byte[] ip = ua.Address.GetAddressBytes();
                            byte[] mk = ua.IPv4Mask.GetAddressBytes();
                            byte[] bc = new byte[4];
                            for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mk[i]);
                            IPAddress bcast = new IPAddress(bc);
                            if (bcast.Equals(IPAddress.Any)) continue;
                            udp.Send(payload, payload.Length, new IPEndPoint(bcast, BEACON_PORT));
                        }
                    }
                }
                catch { }
                Thread.Sleep(1000);
            }
            try { if (udp != null) udp.Close(); } catch { }
        }

        static void WriteI32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        static void WriteI16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    // ==================== 重采样 ====================

    /// <summary>
    /// 重采样器。
    /// 降采样（step &gt; 1）走"面积平均"：每个输出点取它覆盖的那段源区间的加权平均，
    /// 这本身就是一个方框低通，自带抗混叠 —— 原来的实现直接线性插值，
    /// 降采样时 22kHz 以上的内容会折返成混叠噪声。
    /// 升采样（step &lt; 1）没有混叠风险，仍用线性插值。
    /// </summary>
    class Resampler
    {
        readonly int srcCh, dstCh;
        readonly double step;
        double pos;
        float[] last;
        bool hasLast;
        float[] work;

        public Resampler(int srcRate, int srcCh, int dstRate, int dstCh)
        {
            this.srcCh = srcCh;
            this.dstCh = dstCh;
            this.step = (double)srcRate / dstRate;
            this.last = new float[srcCh];
        }

        public int Process(float[] src, int srcFrames, short[] output)
        {
            if (srcFrames <= 0) return 0;
            int frames = srcFrames + (hasLast ? 1 : 0);
            int need = frames * srcCh;
            if (work == null || work.Length < need) work = new float[need];

            int w = 0;
            if (hasLast) { for (int c = 0; c < srcCh; c++) work[w++] = last[c]; }
            Array.Copy(src, 0, work, w, srcFrames * srcCh);

            int outN = 0, maxOut = output.Length;
            bool areaMode = step > 1.0;

            while (true)
            {
                if (outN + dstCh > maxOut) break;

                if (areaMode)
                {
                    double a0 = pos, a1 = pos + step;
                    int k0 = (int)Math.Floor(a0);
                    int k1 = (int)Math.Floor(a1);
                    if (k1 >= frames) break;
                    if (k0 < 0) k0 = 0;

                    if (srcCh == dstCh)
                    {
                        for (int c = 0; c < dstCh; c++)
                        {
                            double acc = 0, wt = 0;
                            for (int k = k0; k <= k1; k++)
                            {
                                double lo = Math.Max(a0, k), hi = Math.Min(a1, k + 1);
                                double ww = hi - lo;
                                if (ww <= 0) continue;
                                acc += work[k * srcCh + c] * ww;
                                wt += ww;
                            }
                            output[outN++] = ToI16(wt > 0 ? (float)(acc / wt) : 0f);
                        }
                    }
                    else if (srcCh == 2 && dstCh == 1)
                    {
                        double acc = 0, wt = 0;
                        for (int k = k0; k <= k1; k++)
                        {
                            double lo = Math.Max(a0, k), hi = Math.Min(a1, k + 1);
                            double ww = hi - lo;
                            if (ww <= 0) continue;
                            acc += ((work[k * 2] + work[k * 2 + 1]) * 0.5f) * ww;
                            wt += ww;
                        }
                        output[outN++] = ToI16(wt > 0 ? (float)(acc / wt) : 0f);
                    }
                    else if (srcCh == 1 && dstCh == 2)
                    {
                        double acc = 0, wt = 0;
                        for (int k = k0; k <= k1; k++)
                        {
                            double lo = Math.Max(a0, k), hi = Math.Min(a1, k + 1);
                            double ww = hi - lo;
                            if (ww <= 0) continue;
                            acc += work[k] * ww;
                            wt += ww;
                        }
                        short sv = ToI16(wt > 0 ? (float)(acc / wt) : 0f);
                        output[outN++] = sv;
                        output[outN++] = sv;
                    }
                    else break;
                }
                else
                {
                    int i0 = (int)pos;
                    if (i0 + 1 >= frames) break;
                    double frac = pos - i0;
                    int a = i0 * srcCh, b = (i0 + 1) * srcCh;

                    if (srcCh == dstCh)
                    {
                        for (int c = 0; c < dstCh; c++)
                        {
                            float v0 = work[a + c], v1 = work[b + c];
                            output[outN++] = ToI16(v0 + (v1 - v0) * (float)frac);
                        }
                    }
                    else if (srcCh == 2 && dstCh == 1)
                    {
                        float m0 = (work[a] + work[a + 1]) * 0.5f;
                        float m1 = (work[b] + work[b + 1]) * 0.5f;
                        output[outN++] = ToI16(m0 + (m1 - m0) * (float)frac);
                    }
                    else if (srcCh == 1 && dstCh == 2)
                    {
                        float v0 = work[a], v1 = work[b];
                        short sv = ToI16(v0 + (v1 - v0) * (float)frac);
                        output[outN++] = sv;
                        output[outN++] = sv;
                    }
                    else break;
                }
                pos += step;
            }

            for (int c = 0; c < srcCh; c++) last[c] = work[(frames - 1) * srcCh + c];
            hasLast = true;
            pos -= (frames - 1);
            if (pos < 0) pos = 0;
            return outN;
        }

        static short ToI16(float f)
        {
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            return (short)(f * 32767f);
        }
    }

    // ==================== 计时 ====================

    /// Windows 默认定时器精度 15.6ms。这台机器上 timeBeginPeriod(1) 已失效
    /// （实测 Sleep(1) 仍要 15.55ms），所以真正干活的是下面的高精度等待定时器；
    /// 这个调用留着，是为了在仍然支持它的系统上让其它地方的 Sleep 也准一点。
    static class WinTimer
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint TimeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint TimeEndPeriod(uint ms);

        public static void Begin() { try { TimeBeginPeriod(1); } catch { } }
        public static void End() { try { TimeEndPeriod(1); } catch { } }
    }

    /// 高精度等待定时器：Thread.Sleep 最小粒度 15.6ms，做不了 10ms 音频节拍。
    /// Windows 10 1803+ 的 CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 实测周期 10.3ms。
    static class HiResTimer
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr sec, IntPtr name, uint flags, uint access);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        const uint HIGH_RESOLUTION = 0x00000002;
        const uint TIMER_ALL_ACCESS = 0x001F0003;
        const uint INFINITE = 0xFFFFFFFF;

        public static IntPtr Create(int periodMs)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return IntPtr.Zero;
                long due = -(long)periodMs * 10000L;
                if (!SetWaitableTimer(h, ref due, periodMs, IntPtr.Zero, IntPtr.Zero, false))
                {
                    CloseHandle(h);
                    return IntPtr.Zero;
                }
                return h;
            }
            catch
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) { try { CloseHandle(h); } catch { } }
                return IntPtr.Zero;
            }
        }

        public static void Wait(IntPtr h) { if (h != IntPtr.Zero) WaitForSingleObject(h, INFINITE); }
        public static void Close(IntPtr h) { if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } } }
    }
}
