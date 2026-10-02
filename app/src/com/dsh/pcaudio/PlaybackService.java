package com.dsh.pcaudio;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;

/**
 * 常驻前台服务。
 *
 * 为什么必须有它：荣耀/HarmonyOS 有自己的 AudioHardening 后台管控，实测日志里会直接打印
 *   AudioHardening background playback would be muted for com.dsh.pcaudio (...)
 * 也就是说，只要这个 app 一退到后台（或者锁屏），系统就把它的声音静音掉 ——
 * 而 socket 还是好的、电脑端还在正常发数据，表现就是"连着但没声音"。
 *
 * 声明一个 foregroundServiceType=mediaPlayback 的前台服务，就是告诉系统
 * "这是个正在放媒体的应用"，系统才会放行后台播放。
 * 本服务本身不碰音频，只管住这个身份和通知栏。
 */
public class PlaybackService extends Service {

    private static final String CHANNEL_ID = "pcaudio_playback";
    private static final int NOTIFY_ID = 0x5043;

    @Override
    public void onCreate() {
        super.onCreate();
        if (Build.VERSION.SDK_INT >= 26) {
            try {
                NotificationChannel ch = new NotificationChannel(
                        CHANNEL_ID, "电脑声音播放", NotificationManager.IMPORTANCE_LOW);
                ch.setShowBadge(false);
                ch.setSound(null, null);
                NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
                if (nm != null) nm.createNotificationChannel(ch);
            } catch (Exception ignored) { }
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        try {
            Notification.Builder b;
            if (Build.VERSION.SDK_INT >= 26) b = new Notification.Builder(this, CHANNEL_ID);
            else b = new Notification.Builder(this);
            b.setContentTitle("电脑声音")
             .setContentText("正在把电脑声音播到这台手机")
             .setSmallIcon(android.R.drawable.ic_media_play)
             .setOngoing(true);
            startForeground(NOTIFY_ID, b.build());
        } catch (Exception ignored) { }
        // 不自启：进程真被杀了就让它安静地结束，别留一个空壳通知
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }
}