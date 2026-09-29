package com.apkdrop

import android.content.Context
import java.security.SecureRandom
import java.util.UUID

const val HTTP_PORT = 8765
const val DISCOVERY_PORT = 8766

class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("apkdrop", Context.MODE_PRIVATE)

    /** Постоянный идентификатор телефона — по нему ПК узнаёт устройство при смене IP. */
    val deviceId: String
        get() = sp.getString("device_id", null) ?: UUID.randomUUID().toString().also {
            sp.edit().putString("device_id", it).apply()
        }

    val pin: String
        get() = sp.getString("pin", null) ?: newPin()

    fun newPin(): String {
        val pin = "%06d".format(SecureRandom().nextInt(1_000_000))
        sp.edit().putString("pin", pin).apply()
        return pin
    }

    var autostart: Boolean
        get() = sp.getBoolean("autostart", true)
        set(value) = sp.edit().putBoolean("autostart", value).apply()

    /** Сервер должен работать — чтобы поднять его после перезагрузки или самообновления. */
    var serverEnabled: Boolean
        get() = sp.getBoolean("server_enabled", true)
        set(value) = sp.edit().putBoolean("server_enabled", value).apply()
}
