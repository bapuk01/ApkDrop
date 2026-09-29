package com.apkdrop

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Installer.onResult(intent)
    }
}

/** Поднимает сервер после перезагрузки и после обновления самого ApkDrop. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val prefs = Prefs(context)
        val boot = intent.action == Intent.ACTION_BOOT_COMPLETED
        if (!prefs.serverEnabled || (boot && !prefs.autostart)) return
        runCatching { DropService.start(context) }
    }
}
