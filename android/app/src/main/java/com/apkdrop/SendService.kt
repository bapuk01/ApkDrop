package com.apkdrop

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import kotlin.concurrent.thread

/**
 * Рассылка идёт в foreground-сервисе, чтобы не прерываться при выключении экрана
 * или сворачивании приложения. Перед запуском вызывающий занимает [SendState.busy] через tryBegin().
 */
class SendService : Service() {

    companion object {
        private const val NOTIFY_ID = 2

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, SendService::class.java))
        }
    }

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(AppLocale.wrap(newBase))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        DropService.createChannels(this)
        val notification = notification(getString(R.string.send_notif_title))
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFY_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFY_ID, notification)
        }

        @Suppress("DEPRECATION")
        wifiLock = getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "apkdrop:send").apply {
                setReferenceCounted(false)
                acquire()
            }
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "apkdrop:send").apply {
                setReferenceCounted(false)
                acquire(30 * 60_000L)
            }

        thread(name = "apkdrop-send") {
            try {
                Sender.run(this) { text ->
                    runCatching {
                        getSystemService(NotificationManager::class.java).notify(NOTIFY_ID, notification(text))
                    }
                }
            } catch (e: Throwable) {
                SendState.log(getString(R.string.sl_conn_error, "", e.message ?: e.javaClass.simpleName), false)
            } finally {
                SendState.progress.value = 0f
                SendState.busy.value = false
                runCatching { wifiLock?.release() }
                runCatching { wakeLock?.release() }
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, DropService.CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_notify)
            .setContentTitle(getString(R.string.send_notif_title))
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }
}
