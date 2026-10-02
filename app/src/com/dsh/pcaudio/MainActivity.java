package com.dsh.pcaudio;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.media.AudioAttributes;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;

public class MainActivity extends Activity {

    public static final int DEFAULT_PORT = 28700;
    public static final int BEACON_PORT = 28701;
    private static final int MAGIC = 0x32414350;   // "PCA2"
    private static final String BEACON_TAG = "PCAUDIO1";
    private static final String OPUS_MIME = "audio/opus";
    private static final String LOGTAG = "pcaudio";

    private TextView statusView, linkTypeView, formatView, detailView, addrView, volView, errorView;
    private volatile int reconnectCount;
    private SeekBar volBar;
    private Button b16, b32, b64, b96, b128, b192, b256, b320, bXHigh;

    private volatile OutputStream currentOut;
    private volatile boolean curIsOpus;
    private volatile int curBitrate;
    private volatile int curCodecId = -1;   // 服务端握手头里报的编码
    private int prefBitrate;
    private int prefCodec;                  // 1=Opus, 0=无损 PCM
    private int prefPcmRate;                // 无损采样率 48000 / 44100
    private volatile int codecFixTries;     // 编码对齐尝试次数，防止无限重连
    private volatile int connTotal;                // 累计连接次数（诊断）
    private volatile String lastEndReason = "—";    // 上一次连接是怎么结束的（诊断）
    private Button bOpus, bPcm, b48k, b441;
    private Button bLow, bMid, bHigh;
    private final Handler ui = new Handler(Looper.getMainLooper());

    private volatile boolean running = true;
    private volatile boolean reconnectRequested = false;
    private volatile AudioTrack currentTrack;
    private volatile Socket currentSocket;
    private volatile float volume = 1.0f;
    private volatile int bufferMs;

    // 播放状态（每次连接重置）
    private boolean playing;
    private final byte[] skipBuf = new byte[2048];
    private long pcmQueued, preTarget;
    private long framesWritten;   // 已写进 AudioTrack 的 PCM 帧数（用来算积压）
    private long rxFrames;        // 最近一秒收到的音频帧数（诊断显示）
    private int feedCount;        // feedPcm 调用计数，用来定期做积压检查
    private long lastHead, lastHeadMs;   // 上次看到的播放头位置/时间，用来发现播放通路卡死
    private boolean btOutput;            // 当前是不是蓝牙输出（决定缓冲和低延迟策略）

    private Thread worker;
    private SharedPreferences prefs;
    private String intentHost;
    private int currentPort, discoveredPort;
    private int attempt;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        prefs = getSharedPreferences("pcaudio", MODE_PRIVATE);

        String h = getIntent().getStringExtra("host");
        intentHost = (h == null || h.trim().length() == 0) ? null : h.trim();
        currentPort = getIntent().getIntExtra("port", prefs.getInt("port", DEFAULT_PORT));
        bufferMs = getIntent().getIntExtra("buffer", prefs.getInt("buffer", 40));
        volume = prefs.getInt("volume", 100) / 100f;
        prefBitrate = prefs.getInt("bitrate", 64);
        prefCodec = prefs.getInt("codec", 1);          // 默认 Opus
        prefPcmRate = prefs.getInt("pcmrate", 48000);
        // 下限放到 20ms。以前卡在 60ms，等于把"25ms 极低""40ms 低延迟"两个挡位
        // 和启动器传来的 --ei buffer 40 一起吃掉了。
        // 现在电脑端是按 10ms 均匀发帧（不再一阵一阵地灌），小缓冲才真正可用。
        if (bufferMs < 20) bufferMs = 20;
        if (bufferMs > 1500) bufferMs = 1500;
        if (currentPort < 1 || currentPort > 65535) currentPort = DEFAULT_PORT;

        buildUi();
        startPlaybackService();
        android.util.Log.w(LOGTAG, "onCreate: 启动工作线程 (this=" + System.identityHashCode(this) + ")");
        worker = new Thread(new Runnable() { @Override public void run() { loop(); } }, "pcaudio");
        worker.start();
    }

    // 起前台服务，否则一旦退到后台/锁屏，荣耀的 AudioHardening 会直接把我们的声音静音，
    // 而 socket 还连着、电脑端还在发数据 —— 用户看到的就是"连着但没声音"。
    private void startPlaybackService() {
        try {
            Intent si = new Intent(this, PlaybackService.class);
            if (Build.VERSION.SDK_INT >= 26) startForegroundService(si);
            else startService(si);
        } catch (Exception ignored) { }
    }

    private void buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.parseColor("#12141A"));
        root.setGravity(Gravity.CENTER);
        int pad = dp(24);
        root.setPadding(pad, pad, pad, pad);

        TextView title = new TextView(this);
        title.setText("电脑声音");
        title.setTextColor(Color.WHITE);
        title.setTextSize(26f);
        title.setGravity(Gravity.CENTER);
        root.addView(title);

        statusView = addText(root, "#8AB4F8", 17f, 18);
        linkTypeView = addText(root, "#81C995", 15f, 8);
        formatView = addText(root, "#9AA0A6", 14f, 14);
        detailView = addText(root, "#5F6368", 12f, 6);

        volView = addText(root, "#E8EAED", 14f, 26);
        volBar = new SeekBar(this);
        volBar.setMax(100);
        volBar.setProgress(Math.round(volume * 100));
        root.addView(volBar, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        volBar.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar sb, int p, boolean fromUser) {
                volume = p / 100f;
                volView.setText("音量  " + p + "%");
                AudioTrack t = currentTrack;
                if (t != null) { try { t.setVolume(volume); } catch (Exception ignored) { } }
                if (fromUser) prefs.edit().putInt("volume", p).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar sb) { }
            @Override public void onStopTrackingTouch(SeekBar sb) { }
        });
        volView.setText("音量  " + Math.round(volume * 100) + "%");

        TextView bufLabel = addText(root, "#E8EAED", 14f, 24);
        bufLabel.setText("缓冲（越大越不卡，但延迟更高）");

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER);
        bLow  = mkBtn("25ms\n极低");
        bMid  = mkBtn("40ms\n低延迟");
        bHigh = mkBtn("80ms\n平衡");
        bXHigh = mkBtn("150ms\n抗卡顿");
        row.addView(bLow); row.addView(bMid); row.addView(bHigh); row.addView(bXHigh);
        root.addView(row);

        bLow.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBuffer(25); } });
        bMid.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBuffer(40); } });
        bHigh.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBuffer(80); } });
        bXHigh.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBuffer(150); } });

        TextView codecLabel = addText(root, "#E8EAED", 14f, 22);
        codecLabel.setText("编码（无损 = 不压缩直传，码率与延迟都最优，但吃带宽）");
        LinearLayout ccRow = new LinearLayout(this);
        ccRow.setOrientation(LinearLayout.HORIZONTAL);
        ccRow.setGravity(Gravity.CENTER);
        bOpus = mkBtn("Opus 压缩");
        bPcm  = mkBtn("无损 PCM");
        ccRow.addView(bOpus); ccRow.addView(bPcm);
        root.addView(ccRow);
        bOpus.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setCodecPref(1); } });
        bPcm.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setCodecPref(0); } });

        LinearLayout prRow = new LinearLayout(this);
        prRow.setOrientation(LinearLayout.HORIZONTAL);
        prRow.setGravity(Gravity.CENTER);
        b48k = mkBtn("无损 48000Hz\n原生不重采样");
        b441 = mkBtn("无损 44100Hz\n对齐蓝牙链路");
        prRow.addView(b48k); prRow.addView(b441);
        root.addView(prRow);
        b48k.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setPcmRatePref(48000); } });
        b441.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setPcmRatePref(44100); } });

        TextView brLabel = addText(root, "#E8EAED", 14f, 22);
        brLabel.setText("码率（只对 Opus 有效；越低越不卡）");
        LinearLayout brRow = new LinearLayout(this);
        brRow.setOrientation(LinearLayout.HORIZONTAL);
        brRow.setGravity(Gravity.CENTER);
        b16 = mkBtn("16k"); b32 = mkBtn("32k"); b64 = mkBtn("64k"); b96 = mkBtn("96k");
        b128 = mkBtn("128k"); b192 = mkBtn("192k"); b256 = mkBtn("256k"); b320 = mkBtn("320k");
        brRow.addView(b16); brRow.addView(b32); brRow.addView(b64); brRow.addView(b96);
        root.addView(brRow);
        LinearLayout brRow2 = new LinearLayout(this);
        brRow2.setOrientation(LinearLayout.HORIZONTAL);
        brRow2.setGravity(Gravity.CENTER);
        brRow2.addView(b128); brRow2.addView(b192); brRow2.addView(b256); brRow2.addView(b320);
        root.addView(brRow2);
        b16.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(16); } });
        b32.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(32); } });
        b64.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(64); } });
        b96.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(96); } });
        b128.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(128); } });
        b192.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(192); } });
        b256.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(256); } });
        b320.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { setBitratePref(320); } });


        errorView = addText(root, "#F28B82", 12f, 14);
        addrView = addText(root, "#3C4043", 11f, 22);

        setContentView(root);
        refreshBufferButtons();
        refreshBitrateButtons();
        refreshCodecButtons();
    }

    private TextView addText(LinearLayout parent, String color, float size, int topPad) {
        TextView tv = new TextView(this);
        tv.setTextColor(Color.parseColor(color));
        tv.setTextSize(size);
        tv.setGravity(Gravity.CENTER);
        tv.setPadding(0, dp(topPad), 0, 0);
        parent.addView(tv);
        return tv;
    }

    private Button mkBtn(String text) {
        Button b = new Button(this);
        b.setText(text);
        b.setTextSize(12f);
        b.setAllCaps(false);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        lp.setMargins(dp(4), dp(6), dp(4), 0);
        b.setLayoutParams(lp);
        return b;
    }

    private void setBuffer(int ms) {
        if (ms == bufferMs) return;
        bufferMs = ms;
        prefs.edit().putInt("buffer", ms).apply();
        refreshBufferButtons();
        requestReconnect();
    }

    private void requestReconnect() {
        reconnectRequested = true;
        Socket s = currentSocket;
        if (s != null) { try { s.close(); } catch (Exception ignored) { } }
    }

    private void refreshBufferButtons() {
        ui.post(new Runnable() {
            @Override public void run() {
                int on = Color.parseColor("#8AB4F8"), off = Color.parseColor("#5F6368");
                bLow.setTextColor(bufferMs == 25 ? on : off);
                bMid.setTextColor(bufferMs == 40 ? on : off);
                bHigh.setTextColor(bufferMs == 80 ? on : off);
                bXHigh.setTextColor(bufferMs == 150 ? on : off);
            }
        });
    }

    private boolean isUsb(String host) {
        return host.startsWith("127.") || host.equalsIgnoreCase("localhost");
    }

    // 当前输出是不是蓝牙（A2DP / SCO / BLE）。
    // 蓝牙跟扬声器/有线完全是两套脾气：延迟高、缓冲需求大、还带一层重采样+编码，
    // 拿同一套参数去套它一定出问题。
    private boolean isBluetoothOutput() {
        try {
            AudioManager am = (AudioManager) getSystemService(Context.AUDIO_SERVICE);
            if (am == null) return false;
            AudioDeviceInfo[] outs = am.getDevices(AudioManager.GET_DEVICES_OUTPUTS);
            for (AudioDeviceInfo d : outs) {
                int t = d.getType();
                if (t == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP) return true;
                if (t == AudioDeviceInfo.TYPE_BLUETOOTH_SCO) return true;
                if (Build.VERSION.SDK_INT >= 31 &&
                        (t == AudioDeviceInfo.TYPE_BLE_HEADSET || t == AudioDeviceInfo.TYPE_BLE_SPEAKER)) return true;
            }
        } catch (Exception ignored) { }
        return false;
    }

    private List<String> candidates() {
        LinkedHashSet<String> set = new LinkedHashSet<String>();
        // 启动器明确给了地址就优先它（USB 模式给的是 127.0.0.1，
        // 这样"优先 USB"的意图才不会被上次记住的 Wi-Fi 地址抢走）
        if (intentHost != null) set.add(intentHost);
        String last = prefs.getString("last_host", null);
        if (last != null) set.add(last);
        set.add("127.0.0.1");
        return new ArrayList<String>(set);
    }

    private void loop() {
        while (running) {
            reconnectRequested = false;
            attempt = 0;
            android.util.Log.w(LOGTAG, "loop: 新一轮, 候选=" + candidates());
            boolean everConnected = false;
            boolean triedAny = false;
            List<String> hosts = candidates();
            String usedHost = null;

            for (int i = 0; i < hosts.size() && running && !reconnectRequested; i++) {
                String h = hosts.get(i);
                // 只有"最后一个候选"才允许报错。
                // 否则 USB 隧道一掉，每一轮都会先去撞一次 127.0.0.1 失败并计一次断开，
                // 看起来就是"不停重连" —— 实际上它后面已经退回 Wi-Fi 连上了。
                boolean lastOne = (i == hosts.size() - 1);
                setStatus("尝试 " + h + " ...");
                android.util.Log.w(LOGTAG, "loop: 试 " + h + ":" + currentPort + " (last=" + lastOne + ")");
                triedAny = true;
                if (tryStream(h, currentPort, lastOne) == 1) {
                    everConnected = true;
                    usedHost = h;
                    saveHost(h, currentPort);
                    break;
                }
            }

            // 首选地址失败、但备用地址连上了：这不是"断开"，是换了条路，
            // 必须明说，不能静默降级（用户最早怀疑的"USB 偷偷变 Wi-Fi"就是这个）
            if (usedHost != null && intentHost != null && !usedHost.equals(intentHost)) {
                setDetail("注意：首选 " + intentHost + " 连不上（USB 隧道多半掉了），已改走 "
                          + (isUsb(usedHost) ? "USB" : "Wi-Fi " + usedHost));
            }

            if (!everConnected && running && !reconnectRequested) {
                setStatus("正在搜索电脑 ...");
                setLinkType("监听 UDP " + BEACON_PORT + " 广播", "#FDD663");
                String found = discover(3000);
                if (found == null) setDetail("广播没收到电脑的回应（可能被 VPN 拦了）");
                if (found != null && running) {
                    setStatus("发现电脑 " + found);
                    if (tryStream(found, discoveredPort, true) == 1) {
                        currentPort = discoveredPort;
                        saveHost(found, discoveredPort);
                        everConnected = true;
                    }
                }
            }

            if (!running) break;
            if (reconnectRequested) { setStatus("按新缓冲重连 ..."); continue; }
            attempt++;
            setStatus((everConnected ? "已断开" : "没找到电脑") + "，第 " + attempt + " 次重试...");
            try { Thread.sleep(2000); } catch (InterruptedException ignored) { }
        }
    }

    private void saveHost(String host, int port) {
        if (host == null || isUsb(host)) return;
        prefs.edit().putString("last_host", host).putInt("port", port).apply();
    }

    private int tryStream(String host, int port, boolean reportError) {
        connTotal++;
        long t0 = System.currentTimeMillis();
        Socket sock = null;
        AudioTrack track = null;
        MediaCodec codec = null;
        boolean connected = false;
        try {
            sock = new Socket();
            sock.connect(new InetSocketAddress(host, port), 2000);
            sock.setTcpNoDelay(true);
            // 关键：没有读超时的话，链路"半死"（TCP 还在但不发数据）时
            // read() 会永久阻塞，重连逻辑永远跑不到。
            sock.setSoTimeout(8000);
            currentSocket = sock;

            InputStream in = sock.getInputStream();
            byte[] hdr = new byte[20];
            if (!readFully(in, hdr, 20)) throw new Exception("握手头不完整");

            int magic = le32(hdr, 0);
            int codecId = le32(hdr, 4);
            int rate = le32(hdr, 8);
            int ch = le16(hdr, 12);
            int bits = le16(hdr, 14);
            int srvBitrate = le32(hdr, 16);

            if (magic != MAGIC) throw new Exception("协议不匹配 0x" + Integer.toHexString(magic));
            if (ch < 1 || ch > 2) throw new Exception("不支持声道数 " + ch);
            if (bits != 16) throw new Exception("只支持 16bit");
            if (rate < 8000 || rate > 192000) throw new Exception("不支持采样率 " + rate);
            // 能成功读到头就说明连上了。之前是等接收循环"正常结束"才置位，
            // 导致中途断开被当成"从没连上过"，于是每次都要去做 6 秒广播搜索。
            connected = true;
            boolean useOpus = (codecId == 1);
            if (useOpus && rate != 48000) throw new Exception("Opus 需要 48000Hz");

            int channelMask = (ch == 1) ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
            int frameSize = ch * 2;
            int minBuf = AudioTrack.getMinBufferSize(rate, channelMask, AudioFormat.ENCODING_PCM_16BIT);
            boolean btOut = isBluetoothOutput();

            // 蓝牙链路的固有延迟是 100~300ms，而且音频要经过 重采样 -> 编码 -> 空口发送，
            // 它根本给不了低延迟。原来我不分青红皂白把 AudioTrack 缓冲压到 25~40ms、
            // 还要求 PERFORMANCE_MODE_LOW_LATENCY —— 在蓝牙上这等于把 BT 编码器饿死，
            // AudioFlinger 只能不停往链路里补静音，听感就是发糊、发虚、像蒙了一层。
            // 所以蓝牙走一套单独的参数：缓冲给足，不要求低延迟，也不去缩缓冲。
            int effBufMs = btOut ? Math.max(bufferMs, 250) : bufferMs;
            int bufSize = Math.max(minBuf, rate * frameSize * effBufMs / 1000);

            AudioTrack.Builder b = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build())
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(rate)
                            .setChannelMask(channelMask)
                            .build())
                    .setBufferSizeInBytes(bufSize)
                    .setTransferMode(AudioTrack.MODE_STREAM);
            if (Build.VERSION.SDK_INT >= 26 && !btOut) b.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY);
            track = b.build();
            track.setVolume(volume);

            // 安卓 7+ 允许运行时缩小缓冲，用来突破 ROM 默认的大缓冲（这台荣耀是 120ms）。
            // 但蓝牙上绝不能缩：缩了就直接饿死 BT 编码器。
            if (Build.VERSION.SDK_INT >= 24 && !btOut) {
                try {
                    int wantFrames = rate * bufferMs / 1000;
                    if (wantFrames > 0) {
                        int got = track.setBufferSizeInFrames(wantFrames);
                        if (got > 0) bufSize = got * frameSize;
                    }
                } catch (Exception ignored) { }
            }
            final int actualBufMs = bufSize * 1000 / (rate * frameSize);
            btOutput = btOut;
            currentTrack = track;

            playing = false;
            pcmQueued = 0;
            framesWritten = 0;
            rxFrames = 0;
            feedCount = 0;
            lastHead = 0;
            lastHeadMs = System.currentTimeMillis();
            // 预启缓冲。蓝牙上原来那 12ms 开播必然立刻饿死，但 120ms 又太奢侈 ——
            // 这部分是**纯加在延迟上的**，蓝牙链路本身已经有 150~300ms 了，给它 60ms 够用。
            int preMs = btOut ? 60 : (bufferMs / 3);
            if (!btOut && preMs > 12) preMs = 12;
            if (preMs < 5) preMs = 5;
            preTarget = (long) rate * frameSize * preMs / 1000;
            if (preTarget < frameSize * 12) preTarget = frameSize * 12;

            setLinkType(btOut
                            ? (isUsb(host) ? "USB 隧道 · 蓝牙输出" : "Wi-Fi · 蓝牙输出")
                            : (isUsb(host) ? "USB 连接（adb 隧道）" : "Wi-Fi 局域网连接"),
                    "#81C995");
            setStatus("已连接");
            formatView.setText(rate + " Hz / " + ch + " 声道 / "
                    + (useOpus ? ("Opus " + srvBitrate + " kbps") : "无损 PCM"));
            addrView.setText("端口 " + port);
            curIsOpus = useOpus;
            curBitrate = srvBitrate;
            curCodecId = codecId;
            currentOut = sock.getOutputStream();

            // ---- 设置对齐 ----
            // 规则：**电脑端是唯一权威，手机是遥控器。**
            // 连上就以电脑端实际在发的为准，手机这边跟着刷新显示，绝不反过来强推自己的偏好。
            // （之前两端都能改同一组设置、又都想赢，就会互相打架，
            //   表现就是"两边设置对不上"，甚至死循环重连）
            // 只有用户在手机上**主动点**按钮时，才发命令去改电脑端。
            boolean prefChanged = false;
            if (prefCodec != codecId) {
                prefCodec = codecId;
                prefs.edit().putInt("codec", codecId).apply();
                prefChanged = true;
            }
            if (!useOpus && (rate == 44100 || rate == 48000) && prefPcmRate != rate) {
                prefPcmRate = rate;
                prefs.edit().putInt("pcmrate", rate).apply();
                prefChanged = true;
            }
            if (useOpus && srvBitrate > 0 && prefBitrate != srvBitrate) {
                prefBitrate = srvBitrate;
                prefs.edit().putInt("bitrate", srvBitrate).apply();
                prefChanged = true;
            }
            codecFixTries = 0;
            curBitrate = useOpus ? srvBitrate : 0;
            if (prefChanged) {
                refreshBitrateButtons();
                refreshCodecButtons();
            }
            refreshBitrateButtons();
            refreshCodecButtons();

            if (useOpus) {
                curBitrate = srvBitrate;
            }

            if (useOpus) {
                codec = MediaCodec.createDecoderByType(OPUS_MIME);
                MediaFormat mf = MediaFormat.createAudioFormat(OPUS_MIME, rate, ch);
                mf.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4096);
                mf.setByteBuffer("csd-0", ByteBuffer.wrap(buildOpusHead(ch, rate)));
                codec.configure(mf, null, null, 0);
                codec.start();

                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
                long ptsUs = 0;
                byte[] lenBuf = new byte[2];
                byte[] pkt = new byte[1500];

                // 积压追赶：允许排在 socket 里的数据量。
                // 原来给了 bps/8（约 125ms）—— 这 125ms 是**纯白送给延迟的**，
                // 而且它是在延迟之外另加的。收紧到 40ms 就够吸收网络抖动了。
                int bps = Math.max(4000, curBitrate * 1000 / 8);
                int backlogLimit = Math.max(256, bps / 25);
                long lastUi = 0;

                while (running && !reconnectRequested) {
                    int inIdx = codec.dequeueInputBuffer(10000);
                    if (inIdx >= 0) {
                        int avail = in.available();
                        int guard = 0;
                        while (avail > backlogLimit && running && !reconnectRequested && guard++ < 300) {
                            if (!readFully(in, lenBuf, 2)) { avail = -1; break; }
                            int sl = (lenBuf[0] & 0xFF) | ((lenBuf[1] & 0xFF) << 8);
                            if (sl <= 0 || sl > 4000) { avail = -1; break; }
                            int left = sl;
                            while (left > 0) {
                                int r = in.read(skipBuf, 0, Math.min(left, skipBuf.length));
                                if (r <= 0) { avail = -1; break; }
                                left -= r;
                            }
                            if (avail < 0) break;
                            avail = in.available();
                        }
                        if (avail < 0) break;

                        long now = System.currentTimeMillis();
                        if (now - lastUi > 1000) {
                            lastUi = now;
                            long rf = rxFrames; rxFrames = 0;
                            // 三个数要分清楚，否则很容易误判延迟来源：
                            //   音轨容量 = AudioTrack 的 buffer 有多大（HAL 给的，改不动，也**不等于延迟**）
                            //   音轨积压 = 里面真的存了多少还没播的音频  ← 这个才是延迟
                            //   网络积压 = 排在 socket 里还没读走的数据
                            long head2 = 0;
                            try { head2 = track.getPlaybackHeadPosition() & 0xFFFFFFFFL; } catch (Exception ignored) { }
                            long trackMs = playing ? ((framesWritten - head2) * 1000L / rate) : 0;
                            if (trackMs < 0) trackMs = 0;
                            setDetail("音轨积压 " + trackMs + "ms  (容量 " + actualBufMs + "ms"
                                      + (btOutput ? "/蓝牙" : "") + ")"
                                      + "   收到 " + rf + " 帧/秒"
                                      + "   网络积压 " + (avail * 1000 / bps) + "ms"
                                      + (guard > 1 ? "  (已追赶)" : ""));
                        }

                        if (!readFully(in, lenBuf, 2)) break;
                        int len = (lenBuf[0] & 0xFF) | ((lenBuf[1] & 0xFF) << 8);
                        if (len <= 0 || len > 4000) throw new Exception("包长度异常 " + len);
                        if (pkt.length < len) pkt = new byte[len];
                        if (!readFully(in, pkt, len)) break;
                        ByteBuffer ib = codec.getInputBuffer(inIdx);
                        ib.clear();
                        ib.put(pkt, 0, len);
                        codec.queueInputBuffer(inIdx, 0, len, ptsUs, 0);
                        ptsUs += 20000;
                        rxFrames++;
                    }
                    int outIdx;
                    while ((outIdx = codec.dequeueOutputBuffer(info, 0)) >= 0) {
                        if (info.size > 0) {
                            ByteBuffer ob = codec.getOutputBuffer(outIdx);
                            byte[] outPcm = new byte[info.size];
                            ob.position(info.offset);
                            ob.limit(info.offset + info.size);
                            ob.get(outPcm);
                            feedPcm(track, outPcm, outPcm.length, frameSize, rate);
                        }
                        codec.releaseOutputBuffer(outIdx, false);
                    }
                }
            } else {
                // 无损 PCM 分支原来完全没有界面刷新，出问题看不到任何信息。
                // 补一个每秒更新的速率显示。
                byte[] buf = new byte[frameSize * 2048];
                long lastUi = 0;
                while (running && !reconnectRequested) {
                    int n = in.read(buf, 0, buf.length);
                    if (n <= 0) break;
                    int usable = n - (n % frameSize);
                    if (usable <= 0) continue;
                    feedPcm(track, buf, usable, frameSize, rate);
                    rxFrames += usable / frameSize;
                    long now = System.currentTimeMillis();
                    if (now - lastUi > 1000) {
                        lastUi = now;
                        long rf = rxFrames; rxFrames = 0;
                        long head3 = 0;
                        try { head3 = track.getPlaybackHeadPosition() & 0xFFFFFFFFL; } catch (Exception ignored) { }
                        long tm = playing ? ((framesWritten - head3) * 1000L / rate) : 0;
                        if (tm < 0) tm = 0;
                        setDetail("音轨积压 " + tm + "ms  (容量 " + actualBufMs + "ms"
                                  + (btOutput ? "/蓝牙" : "") + ")"
                                  + "   收到 " + rf + " 帧/秒");
                    }
                }
            }

            setStatus("已连接");
            setDetail("实际缓冲 " + actualBufMs + " ms (设定 " + bufferMs + ")");
        } catch (Exception e) {
            android.util.Log.w(LOGTAG, "tryStream 异常: " + e.getClass().getSimpleName() + " " + e.getMessage());
            if (!reconnectRequested) {
                lastEndReason = e.getClass().getSimpleName() + " " + (System.currentTimeMillis() - t0) + "ms";
            }
            updateDiag();
            // 只有"最后一个候选"才报错并计数。
            // 否则 USB 隧道一掉，每轮都先在 127.0.0.1 上失败一次并计一次断开，
            // 明明后面已经退回 Wi-Fi 连上了，界面却一直刷"断开 #N"，看着像不停重连。
            if (reportError && !reconnectRequested) {
                reconnectCount++;
                setError("断开 #" + reconnectCount + ": " + e.getClass().getSimpleName() + " " + e.getMessage());
            }
        } finally {
            currentTrack = null;
            currentOut = null;
            currentSocket = null;
            // 断开后码率按钮要跟着失效，否则会留着上一次连接的"生效中"状态，
            // 看着像是能点、其实点了没反应。
            curIsOpus = false;
            curBitrate = 0;
            refreshBitrateButtons();
            try { if (codec != null) { codec.stop(); codec.release(); } } catch (Exception ignored) { }
            try { if (track != null) { track.stop(); track.release(); } } catch (Exception ignored) { }
            try { if (sock != null) sock.close(); } catch (Exception ignored) { }
        }
        android.util.Log.w(LOGTAG, "tryStream 结束: connected=" + connected
                + " reconnectRequested=" + reconnectRequested + " running=" + running);
        if (!reconnectRequested) {
            lastEndReason = "EOF " + (System.currentTimeMillis() - t0) + "ms";
        } else {
            lastEndReason = "主动重连";
        }
        updateDiag();
        return connected ? 1 : 0;
    }

    // 把诊断信息显示到界面上。荣耀把第三方应用的 logcat 全吞了（连 W 级都看不到），
    // 界面文字是唯一能稳定读出来的通道。
    private void updateDiag() {
        ui.post(new Runnable() {
            @Override public void run() {
                if (addrView == null) return;
                addrView.setText("端口 " + currentPort + "   累计连接 " + connTotal
                                 + " 次   上次结束: " + lastEndReason);
            }
        });
    }

    // 先攒够一点缓冲再开播，避免一开始就欠载。
    //
    // 关键：用非阻塞写。用阻塞式 track.write() 时，只要 Android / 手机厂商的音频通路
    // 一旦暂停消费（后台管控、音频焦点被抢、HAL 卡住…），write() 就会永久阻塞，
    // 整个接收循环跟着卡死 —— 表现就是"socket 还连着、服务端还在发，但没声音、
    // 也不重连"。实测就复现过这个死状。改成非阻塞后，缓冲满了就丢掉这一小段，
    // 循环永远能往下走，最多是丢点音频，不会把整条链路拖死。
    private void feedPcm(AudioTrack track, byte[] data, int len, int frameSize, int rate) {
        try {
            int accepted = 0;
            while (accepted < len) {
                int n = track.write(data, accepted, len - accepted, AudioTrack.WRITE_NON_BLOCKING);
                if (n <= 0) break;      // 缓冲满：丢掉剩下的，不等
                accepted += n;
            }
            if (!playing) {
                pcmQueued += accepted;
                if (pcmQueued >= preTarget) {
                    track.play();
                    playing = true;
                    setStatus("已连接");
                }
            }
            framesWritten += (long) accepted / frameSize;
            if ((++feedCount & 0x3F) == 0) flushIfTooFarBehind(track, rate);
        } catch (Exception ignored) { }
    }

    // 两个安全阀，正常情况下都碰不到：
    //  1) 积压离谱（攒了好几百毫秒的延迟）-> 丢掉重新对齐，不然延迟一直降不下来
    //  2) 播放头几秒不动 -> 说明音频通路被系统掐住了（后台管控 / HAL 卡死），
    //     pause+flush+play 把它踢回来。刚才实测卡死那次就是这个现象。
    private void flushIfTooFarBehind(AudioTrack track, int rate) {
        try {
            if (!playing) return;
            long head = track.getPlaybackHeadPosition() & 0xFFFFFFFFL;
            long nowMs = System.currentTimeMillis();

            if (head != lastHead) {
                lastHead = head;
                lastHeadMs = nowMs;
            } else if (!btOutput && nowMs - lastHeadMs > 3000) {
                // 蓝牙通路的播放头本来就会被链路层暂停/抖动，不能拿这条去判它卡死，
                // 否则会周期性误伤、把好好的缓冲 flush 掉。
                track.pause();
                track.flush();
                track.play();
                lastHeadMs = nowMs;
                setDetail("播放通路卡住，已重启");
                return;
            }

            // 电脑和手机是两颗独立时钟，长期跑必然有微小频差，音轨积压会缓慢往上爬。
            // 这条安全阀就是用来把它压住的，阈值必须贴着"可接受延迟"设，
            // 而不是设成 1200ms —— 那样等于允许它一路爬到一秒多，全是白送的延迟。
            long limitMs = btOutput ? 350 : 250;
            long backlog = framesWritten - head;
            if (backlog > (long) rate * limitMs / 1000) {
                track.pause();
                track.flush();
                track.play();
                framesWritten = track.getPlaybackHeadPosition() & 0xFFFFFFFFL;
                lastHead = framesWritten;
                lastHeadMs = nowMs;
                setDetail("积压过多，已重新对齐");
            }
        } catch (Exception ignored) { }
    }

    // 安卓的 Opus 解码器需要 OpusHead 配置块(csd-0)才能正确初始化
    private static byte[] buildOpusHead(int channels, int rate) {
        byte[] h = new byte[19];
        byte[] magic = new byte[] { 'O', 'p', 'u', 's', 'H', 'e', 'a', 'd' };
        System.arraycopy(magic, 0, h, 0, 8);
        h[8] = 1;                                   // version
        h[9] = (byte) channels;
        h[10] = (byte) (312 & 0xFF);                // pre-skip = 312
        h[11] = (byte) ((312 >> 8) & 0xFF);
        h[12] = (byte) (rate & 0xFF);               // 原始输入采样率
        h[13] = (byte) ((rate >> 8) & 0xFF);
        h[14] = (byte) ((rate >> 16) & 0xFF);
        h[15] = (byte) ((rate >> 24) & 0xFF);
        h[16] = 0;                                  // output gain
        h[17] = 0;
        h[18] = 0;                                  // mapping family
        return h;
    }
    private void setBitratePref(int kbps) {
        if (!curIsOpus) { setDetail("当前不是 Opus 模式，码率不可调"); return; }
        prefBitrate = kbps;
        prefs.edit().putInt("bitrate", kbps).apply();
        curBitrate = kbps;
        refreshBitrateButtons();
        sendBitrate(kbps);
    }

    // 注意：必须放到后台线程 —— 安卓禁止在主线程做网络操作
    // 必须单次 write 并加锁：分三次写的话，多次点击会各起一个线程，
    // 字节会交错，把电脑端的命令流彻底搞乱。
    private final Object cmdLock = new Object();

    private void sendBitrate(final int kbps) {
        final OutputStream out = currentOut;
        if (out == null) { setDetail("还没连上电脑，无法改码率"); return; }
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    byte[] cmd = new byte[] { 0x01, (byte) (kbps & 0xFF), (byte) ((kbps >> 8) & 0xFF) };
                    synchronized (cmdLock) {
                        out.write(cmd, 0, 3);
                        out.flush();
                    }
                    setDetail("码率已改为 " + kbps + " kbps");
                } catch (Exception e) {
                    setDetail("改码率失败: " + e.getClass().getSimpleName());
                }
            }
        }, "sendBitrate").start();
    }

    private void refreshBitrateButtons() {
        ui.post(new Runnable() {
            @Override public void run() {
                int on = Color.parseColor("#8AB4F8"), off = Color.parseColor("#5F6368");
                boolean e = curIsOpus;
                b16.setEnabled(e); b32.setEnabled(e); b64.setEnabled(e); b96.setEnabled(e);
                b128.setEnabled(e); b192.setEnabled(e); b256.setEnabled(e); b320.setEnabled(e);
                b16.setTextColor(e && curBitrate == 16 ? on : off);
                b32.setTextColor(e && curBitrate == 32 ? on : off);
                b64.setTextColor(e && curBitrate == 64 ? on : off);
                b96.setTextColor(e && curBitrate == 96 ? on : off);
                b128.setTextColor(e && curBitrate == 128 ? on : off);
                b192.setTextColor(e && curBitrate == 192 ? on : off);
                b256.setTextColor(e && curBitrate == 256 ? on : off);
                b320.setTextColor(e && curBitrate == 320 ? on : off);
            }
        });
    }

    // ---------------- 编码切换 ----------------

    private void setCodecPref(int codecId) {
        if (codecId == prefCodec) return;
        prefCodec = codecId;
        prefs.edit().putInt("codec", codecId).apply();
        codecFixTries = 0;
        refreshCodecButtons();
        sendCmdAndReconnect(0x03, codecId);
    }

    private void setPcmRatePref(int rate) {
        if (rate == prefPcmRate) return;
        prefPcmRate = rate;
        prefs.edit().putInt("pcmrate", rate).apply();
        codecFixTries = 0;
        refreshCodecButtons();
        if (prefCodec != 0) { setDetail("当前不是无损模式，采样率已记住"); return; }
        sendCmdAndReconnect(0x04, rate == 44100 ? 44100 : 48000);
    }

    // 先把命令**真正写出去并 flush 完**，再断开重连。
    // 这个顺序不能反：原来是先丢后台线程去写命令、调用方紧接着就 close() 掉 socket，
    // 命令根本没发出去 —— 电脑端设置没变，下次握手又不一致，就成了无限快速重连。
    private void sendCmdAndReconnect(final int op, final int value) {
        final OutputStream out = currentOut;
        if (out == null) { requestReconnect(); return; }
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    byte[] cmd = new byte[] { (byte) op, (byte) (value & 0xFF), (byte) ((value >> 8) & 0xFF) };
                    synchronized (cmdLock) {
                        out.write(cmd, 0, 3);
                        out.flush();
                    }
                    Thread.sleep(180);   // 留一点时间给对端处理，再断
                } catch (Exception ignored) { }
                requestReconnect();
            }
        }, "cmdReconnect").start();
    }

    private void refreshCodecButtons() {
        ui.post(new Runnable() {
            @Override public void run() {
                int on = Color.parseColor("#8AB4F8"), off = Color.parseColor("#5F6368");
                boolean pcm = (prefCodec == 0);
                bOpus.setTextColor(!pcm ? on : off);
                bPcm.setTextColor(pcm ? on : off);
                b48k.setEnabled(pcm);
                b441.setEnabled(pcm);
                b48k.setTextColor(pcm && prefPcmRate != 44100 ? on : off);
                b441.setTextColor(pcm && prefPcmRate == 44100 ? on : off);
            }
        });
    }

    // 统一的命令发送：必须后台线程（安卓禁止主线程做网络），
    // 必须单次 write 并加锁（分多次写会让字节交错，把电脑端的命令流搞乱）。
    private boolean sendCmd(final int op, final int value) {
        final OutputStream out = currentOut;
        if (out == null) return false;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    byte[] cmd = new byte[] { (byte) op, (byte) (value & 0xFF), (byte) ((value >> 8) & 0xFF) };
                    synchronized (cmdLock) {
                        out.write(cmd, 0, 3);
                        out.flush();
                    }
                } catch (Exception ignored) { }
            }
        }, "sendCmd").start();
        return true;
    }

    private String discover(int timeoutMs) {
        DatagramSocket ds = null;
        discoveredPort = DEFAULT_PORT;
        try {
            ds = new DatagramSocket(null);
            ds.setReuseAddress(true);
            ds.bind(new InetSocketAddress(BEACON_PORT));
            ds.setSoTimeout(timeoutMs);
            byte[] buf = new byte[256];
            long end = System.currentTimeMillis() + timeoutMs;
            while (running && System.currentTimeMillis() < end) {
                DatagramPacket pkt = new DatagramPacket(buf, buf.length);
                try { ds.receive(pkt); } catch (SocketTimeoutException ste) { break; }
                String s = new String(pkt.getData(), 0, pkt.getLength(), "US-ASCII").trim();
                if (s.startsWith(BEACON_TAG)) {
                    String[] parts = s.split("\\s+");
                    if (parts.length > 1) {
                        try { discoveredPort = Integer.parseInt(parts[1]); } catch (Exception ignored) { }
                    }
                    if (discoveredPort < 1 || discoveredPort > 65535) discoveredPort = DEFAULT_PORT;
                    return pkt.getAddress().getHostAddress();
                }
            }
        } catch (Exception ignored) {
        } finally {
            try { if (ds != null) ds.close(); } catch (Exception ignored) { }
        }
        return null;
    }

    private static boolean readFully(InputStream in, byte[] dst, int len) throws Exception {
        int off = 0;
        while (off < len) {
            int n = in.read(dst, off, len - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    private static int le32(byte[] b, int o) {
        return (b[o] & 0xFF) | ((b[o + 1] & 0xFF) << 8) | ((b[o + 2] & 0xFF) << 16) | ((b[o + 3] & 0xFF) << 24);
    }

    private static int le16(byte[] b, int o) {
        return (b[o] & 0xFF) | ((b[o + 1] & 0xFF) << 8);
    }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }

    private void setStatus(final String s) {
        ui.post(new Runnable() { @Override public void run() { statusView.setText(s); } });
    }

    private void setLinkType(final String s, final String color) {
        ui.post(new Runnable() {
            @Override public void run() {
                linkTypeView.setText(s);
                linkTypeView.setTextColor(Color.parseColor(color));
            }
        });
    }

    private void setError(final String s) {
        ui.post(new Runnable() { @Override public void run() { if (errorView != null) errorView.setText(s); } });
    }

    private void setDetail(final String s) {
        ui.post(new Runnable() { @Override public void run() { detailView.setText(s); } });
    }

    @Override
    protected void onDestroy() {
        android.util.Log.w(LOGTAG, "onDestroy (this=" + System.identityHashCode(this) + ")");
        running = false;
        Socket s = currentSocket;
        if (s != null) { try { s.close(); } catch (Exception ignored) { } }
        try { if (worker != null) worker.interrupt(); } catch (Exception ignored) { }
        // 退出就把前台服务一起收掉，别留一个孤零零的通知
        try { stopService(new Intent(this, PlaybackService.class)); } catch (Exception ignored) { }
        super.onDestroy();
    }
}