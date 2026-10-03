package com.apkdrop

import android.content.Context
import android.text.format.Formatter
import org.json.JSONObject
import java.io.IOException

/** Рассылка выбранного APK на отмеченные устройства по очереди. Выполняется в [SendService]. */
object Sender {

    /** [onNotify] получает короткий текст для уведомления («Отправка на Pixel 8…»). */
    fun run(context: Context, onNotify: (String) -> Unit) {
        val apk = SendState.apk.value ?: return
        val targets = SendState.devices.value.filter { it.checked }
        if (targets.isEmpty()) return
        val lang = context.resources.configuration.locales[0].language.ifEmpty { "en" }
        val launch = SendState.launchAfter
        val ownId = Prefs(context).deviceId

        for (target in targets) {
            onNotify(context.getString(R.string.send_notif_text, target.name))
            runCatching { sendTo(context, lang, ownId, apk, target.id, launch) }
                .onFailure {
                    SendState.setStatus(target.id, "✖ " + context.getString(R.string.st_error), false)
                    SendState.log(context.getString(R.string.sl_conn_error, target.name, it.message ?: it.javaClass.simpleName), false)
                }
        }
        SendState.progress.value = 0f
    }

    private fun sendTo(context: Context, lang: String, ownId: String, apk: SelectedApk, id: String, launch: Boolean) {
        var d = SendState.device(id) ?: return
        val name = d.name.ifEmpty { d.host }
        val pin = d.pin
        if (pin.isNullOrEmpty()) {
            SendState.setStatus(id, "✖ " + context.getString(R.string.st_need_pin), false)
            return
        }

        SendState.setStatus(id, context.getString(R.string.st_connecting))
        SendState.progress.value = 0f
        val code = try {
            SenderClient.auth(d.host, d.port, pin, lang)
        } catch (e: IOException) {
            // IP мог смениться (DHCP) — ищем устройство в сети по постоянному id.
            val found = runCatching { SenderClient.discover(ownId) }.getOrDefault(emptyList()).firstOrNull { it.first.optString("id") == id }
            if (found == null) {
                offline(context, d)
                return
            }
            d = SendState.upsert(found.first, found.second)
            try {
                SenderClient.auth(d.host, d.port, pin, lang)
            } catch (e2: IOException) {
                offline(context, d)
                return
            }
        }
        when (code) {
            200 -> Unit
            401 -> {
                // PIN сменили на устройстве — забываем старый, при следующей отправке спросим заново.
                SendState.setPin(id, null)
                SendState.setStatus(id, "✖ " + context.getString(R.string.st_wrong_pin), false)
                SendState.log(context.getString(R.string.sl_pin_wrong, name), false)
                return
            }
            429 -> {
                SendState.setStatus(id, "✖ " + context.getString(R.string.st_locked), false)
                SendState.log(context.getString(R.string.sl_pin_locked, name), false)
                return
            }
            else -> {
                SendState.setStatus(id, "✖ " + context.getString(R.string.st_error), false)
                SendState.log(context.getString(R.string.sl_conn_error, name, "HTTP $code"), false)
                return
            }
        }

        SendState.log(context.getString(R.string.sl_sending, name, apk.fileName, Formatter.formatShortFileSize(context, apk.size)))
        var lastPercent = -1
        val outcome = SenderClient.install(
            d.host, d.port, pin, apk.file, launch, lang,
            onProgress = { p ->
                SendState.progress.value = p
                val percent = (p * 100).toInt()
                if (percent != lastPercent) {
                    lastPercent = percent
                    SendState.setStatus(id, context.getString(R.string.st_sending, percent))
                }
            },
            onEvent = { event -> onEvent(context, id, name, event) },
        )

        val text = when {
            outcome.httpCode != 0 -> context.getString(R.string.err_rejected, outcome.httpCode, outcome.message)
            outcome.selfUpdate -> context.getString(R.string.self_update_ok)
            outcome.noResult -> context.getString(R.string.closed_no_result)
            else -> outcome.message + if (outcome.launched) context.getString(R.string.launched_suffix) else ""
        }
        val ok = outcome.ok
        SendState.setStatus(id, (if (ok) "✔ " else "✖ ") + text, ok)
        SendState.log("$name: $text", ok)
        if (outcome.launchError != null) SendState.log(context.getString(R.string.sl_not_launched, outcome.launchError), null)
    }

    private fun offline(context: Context, d: Remote) {
        SendState.setStatus(d.id, "✖ " + context.getString(R.string.st_offline), false)
        SendState.log(context.getString(R.string.sl_offline, d.name.ifEmpty { d.host }), false)
    }

    private fun onEvent(context: Context, id: String, name: String, event: JSONObject) {
        when (event.optString("event")) {
            "received" -> {
                SendState.progress.value = 1f
                SendState.setStatus(id, context.getString(R.string.st_installing))
            }
            "parsed" -> {
                val label = event.optString("label")
                val version = event.optString("version")
                val code = event.optLong("versionCode")
                val there = if (event.isNull("installedVersionCode")) {
                    context.getString(R.string.sl_not_installed)
                } else {
                    "${event.optString("installedVersion")} (${event.optLong("installedVersionCode")})"
                }
                SendState.log(context.getString(R.string.sl_parsed, name, label, version, code, there))
            }
            "installing" -> SendState.setStatus(id, context.getString(R.string.st_installing))
            "confirm" -> {
                SendState.setStatus(id, "⏳ " + context.getString(R.string.st_confirm))
                SendState.log(context.getString(R.string.sl_confirm, name), null)
            }
            "self-update" -> SendState.log(context.getString(R.string.sl_self_update, name))
        }
    }
}
