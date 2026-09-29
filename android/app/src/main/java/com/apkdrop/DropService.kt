package com.apkdrop

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import java.io.File

/** Foreground-сервис: держит HTTP-сервер и UDP-поиск, пока телефон ждёт APK. */
class DropService : Service() {

    companion object {
        const val CHANNEL_SERVICE = "service"
        const val CHANNEL_CONFIRM = "confirm"
        private const val NOTIFY_ID = 1
        private const val ACTION_STOP = "com.apkdrop.STOP"

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, DropService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, DropService::class.java))
        }

        fun createChannels(context: Context) {
            val nm = context.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_SERVICE, context.getString(R.string.channel_service), NotificationManager.IMPORTANCE_LOW)
            )
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_CONFIRM, context.getString(R.string.channel_confirm), NotificationManager.IMPORTANCE_HIGH)
            )
        }
    }

    private var http: HttpServer? = null
    private var discovery: Discovery? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) = refreshNotification()
        override fun onLost(network: Network) = refreshNotification()
    }

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(AppLocale.wrap(newBase))
    }

    override fun onCreate() {
        super.onCreate()
        createChannels(this)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFY_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFY_ID, buildNotification())
        }

        File(cacheDir, "incoming").listFiles()?.forEach { it.delete() }

        try {
            http = HttpServer(this).also { it.start() }
        } catch (e: Exception) {
            DropState.log(getString(R.string.log_port_failed, HTTP_PORT, e.message.orEmpty()), ok = false)
            stopSelf()
            return
        }
        try {
            discovery = Discovery(this).also { it.start() }
        } catch (e: Exception) {
            DropState.log(getString(R.string.log_discovery_failed, DISCOVERY_PORT, e.message.orEmpty()), ok = false)
        }

        val wifi = applicationContext.getSystemService(WifiManager::class.java)
        @Suppress("DEPRECATION")
        wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "apkdrop:wifi").apply {
            setReferenceCounted(false)
            acquire()
        }
        // Без этого многие телефоны отбрасывают широковещательные пакеты при выключенном экране.
        multicastLock = wifi.createMulticastLock("apkdrop:discovery").apply {
            setReferenceCounted(false)
            acquire()
        }
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(networkCallback)

        DropState.setRunning(true)
        val addr = Net.localAddresses().joinToString { "$it:$HTTP_PORT" }.ifEmpty { getString(R.string.no_network) }
        DropState.log(getString(R.string.log_server_started, addr))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            Prefs(this).serverEnabled = false
            stopSelf()
            return START_NOT_STICKY
        }
        return START_STICKY
    }

    override fun onDestroy() {
        runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(networkCallback) }
        http?.stop()
        discovery?.stop()
        wifiLock?.release()
        multicastLock?.release()
        if (DropState.running.value) DropState.log(getString(R.string.log_server_stopped))
        DropState.setRunning(false)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun refreshNotification() {
        runCatching { getSystemService(NotificationManager::class.java).notify(NOTIFY_ID, buildNotification()) }
    }

    private fun buildNotification(): Notification {
        val addresses = Net.localAddresses()
        val text = if (addresses.isEmpty()) getString(R.string.notif_no_wifi)
        else getString(R.string.notif_waiting, addresses.joinToString { "$it:$HTTP_PORT" })

        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, DropService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_notify)
            .setContentTitle("ApkDrop")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(0, getString(R.string.action_stop), stop)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }
}
