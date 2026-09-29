package com.apkdrop

import android.content.Context
import android.os.Build
import android.provider.Settings
import org.json.JSONObject
import java.net.Inet4Address
import java.net.NetworkInterface

object Net {
    /** IPv4-адреса телефона в локальных сетях (Wi-Fi, точка доступа, USB-модем). */
    fun localAddresses(): List<String> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .filter { it.isSiteLocalAddress }
            .map { it.hostAddress.orEmpty() }
            .distinct()
    } catch (e: Exception) {
        emptyList()
    }

    /** Ответ на /info и на широковещательный поиск. */
    fun info(context: Context): JSONObject = JSONObject()
        .put("app", "apkdrop")
        .put("protocol", 3)
        .put("id", Prefs(context).deviceId)
        .put("name", deviceName(context))
        .put("model", "${Build.MANUFACTURER} ${Build.MODEL}")
        .put("android", Build.VERSION.RELEASE)
        .put("sdk", Build.VERSION.SDK_INT)
        .put("port", HTTP_PORT)
        .put("silentUpdates", Installer.silentUpdatesSupported)
        .put("canInstall", context.packageManager.canRequestPackageInstalls())

    fun deviceName(context: Context): String =
        Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)
            ?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER} ${Build.MODEL}"
}
