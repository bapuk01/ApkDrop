package com.apkdrop.ui

import android.text.format.Formatter
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.apkdrop.HTTP_PORT
import com.apkdrop.Prefs
import com.apkdrop.R
import com.apkdrop.Remote
import com.apkdrop.SendService
import com.apkdrop.SendState
import com.apkdrop.SenderClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.IOException

/** Вкладка «Отправить»: выбрать APK на телефоне и разослать его на другие устройства с ApkDrop. */
@Composable
internal fun SendTab(actions: Actions) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val devices by SendState.devices.collectAsState()
    val apk by SendState.apk.collectAsState()
    val busy by SendState.busy.collectAsState()
    val progress by SendState.progress.collectAsState()
    val log by SendState.log.collectAsState()

    var finding by remember { mutableStateOf(false) }
    var pinFor by remember { mutableStateOf<String?>(null) }
    var resumeSend by remember { mutableStateOf(false) }
    var showAdd by remember { mutableStateOf(false) }
    var launchAfter by remember { mutableStateOf(SendState.launchAfter) }

    val lang = context.resources.configuration.locales[0].language.ifEmpty { "en" }

    fun find() {
        if (finding) return
        finding = true
        scope.launch(Dispatchers.IO) {
            try {
                val found = SenderClient.discover(Prefs(context).deviceId)
                found.forEach { (info, host) -> SendState.upsert(info, host) }
                SendState.markMissing(found.map { it.first.optString("id") }.toSet())
                if (found.isEmpty()) {
                    SendState.log(context.getString(R.string.sl_none), null)
                } else {
                    SendState.log(
                        context.getString(R.string.sl_found, found.joinToString { (info, host) -> "${info.optString("name")} ($host)" }),
                        true,
                    )
                }
            } catch (e: Exception) {
                SendState.log(context.getString(R.string.sl_conn_error, "", e.message ?: e.javaClass.simpleName), false)
            } finally {
                finding = false
            }
        }
    }

    fun addByIp(input: String) {
        scope.launch(Dispatchers.IO) {
            val text = input.trim()
            var host = text
            var port = HTTP_PORT
            val colon = text.lastIndexOf(':')
            if (colon > 0) text.substring(colon + 1).toIntOrNull()?.let { host = text.substring(0, colon); port = it }
            try {
                val info = SenderClient.info(host, port, lang) ?: throw IOException(context.getString(R.string.send_not_apkdrop))
                val device = SendState.upsert(info, host, port)
                SendState.log(context.getString(R.string.sl_added, device.name, host), true)
            } catch (e: Exception) {
                SendState.log(context.getString(R.string.sl_add_failed, "$host:$port", e.message ?: e.javaClass.simpleName), false)
            }
        }
    }

    fun startSend() {
        if (SendState.apk.value == null) {
            SendState.log(context.getString(R.string.send_need_apk), false)
            return
        }
        val targets = SendState.devices.value.filter { it.checked }
        if (targets.isEmpty()) {
            SendState.log(context.getString(R.string.send_need_devices), false)
            return
        }
        // Сначала спрашиваем PIN у тех, для кого он ещё не введён, потом продолжаем отправку.
        targets.firstOrNull { it.pin.isNullOrEmpty() }?.let {
            pinFor = it.id
            resumeSend = true
            return
        }
        if (!SendState.tryBegin()) return
        try {
            SendService.start(context)
        } catch (e: Exception) {
            SendState.busy.value = false
            SendState.log(context.getString(R.string.sl_conn_error, "", e.message ?: e.javaClass.simpleName), false)
        }
    }

    LaunchedEffect(Unit) {
        if (!SendState.autoDiscovered) {
            SendState.autoDiscovered = true
            find()
        }
    }

    pinFor?.let { id ->
        devices.firstOrNull { it.id == id }?.let { device ->
            TextDialog(
                title = stringResource(R.string.pin_dialog_title, device.name.ifEmpty { device.host }),
                message = stringResource(R.string.pin_dialog_text),
                label = "PIN",
                initial = "",
                numeric = true,
                onDismiss = { pinFor = null; resumeSend = false },
                onConfirm = { value ->
                    SendState.setPin(id, value)
                    pinFor = null
                    if (resumeSend) {
                        resumeSend = false
                        startSend()
                    }
                },
            )
        }
    }
    if (showAdd) {
        TextDialog(
            title = stringResource(R.string.add_ip_title),
            message = stringResource(R.string.add_ip_hint),
            label = stringResource(R.string.add_ip_label),
            initial = "",
            numeric = false,
            onDismiss = { showAdd = false },
            onConfirm = { value -> showAdd = false; addByIp(value) },
        )
    }

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(stringResource(R.string.send_apk_title), style = MaterialTheme.typography.titleMedium)
                    val selected = apk
                    if (selected == null) {
                        Text(stringResource(R.string.send_apk_none))
                    } else {
                        Text(selected.label, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                        Text(selected.pkg, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
                        Text(
                            stringResource(
                                R.string.send_apk_version, selected.versionName, selected.versionCode,
                                Formatter.formatShortFileSize(context, selected.size),
                            ),
                            style = MaterialTheme.typography.bodySmall,
                        )
                        Text(selected.fileName, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Button(onClick = actions.pickApk, enabled = !busy, modifier = Modifier.focusRing()) {
                        Text(stringResource(if (selected == null) R.string.send_pick else R.string.send_pick_other))
                    }
                }
            }
        }

        item {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(stringResource(R.string.send_devices_title), style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                        TextButton(onClick = { find() }, enabled = !finding && !busy, modifier = Modifier.focusRing()) {
                            Text(stringResource(if (finding) R.string.send_finding else R.string.send_find))
                        }
                        TextButton(onClick = { showAdd = true }, modifier = Modifier.focusRing()) { Text(stringResource(R.string.send_add_ip)) }
                    }
                    Text(stringResource(R.string.send_devices_hint), style = MaterialTheme.typography.bodySmall)
                    if (devices.isEmpty()) {
                        Text(stringResource(R.string.send_devices_empty), modifier = Modifier.padding(top = 8.dp))
                    }
                    devices.forEach { device ->
                        DeviceRow(device, enabled = !busy, onPin = { pinFor = device.id })
                    }
                }
            }
        }

        item {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(
                            checked = launchAfter,
                            onCheckedChange = { launchAfter = it; SendState.launchAfter = it },
                            modifier = Modifier.focusRing(),
                        )
                        Text(stringResource(R.string.send_launch))
                    }
                    Button(
                        onClick = { startSend() },
                        enabled = !busy && apk != null && devices.any { it.checked },
                        modifier = Modifier.fillMaxWidth().focusRing(),
                    ) {
                        Text(stringResource(if (busy) R.string.send_button_busy else R.string.send_button))
                    }
                    if (busy) LinearProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
                }
            }
        }

        item {
            Text(stringResource(R.string.ui_log), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 4.dp))
        }
        items(log) { LogRow(it) }

        item { Footer() }
    }
}

@Composable
private fun DeviceRow(device: Remote, enabled: Boolean, onPin: () -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Checkbox(
            checked = device.checked,
            onCheckedChange = { SendState.setChecked(device.id, it) },
            enabled = enabled,
            modifier = Modifier.focusRing(),
        )
        Column(Modifier.weight(1f)) {
            Text(device.name.ifEmpty { device.host }, fontWeight = FontWeight.Medium)
            val details = buildString {
                append(device.host).append(':').append(device.port)
                if (device.model.isNotEmpty() && device.model != device.name) append(" · ").append(device.model)
            }
            Text(details, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (device.status.isNotEmpty()) {
                Text(
                    device.status,
                    style = MaterialTheme.typography.bodySmall,
                    color = when (device.statusOk) {
                        true -> okColor
                        false -> errColor
                        null -> Color.Unspecified
                    },
                )
            }
        }
        // «PIN ✓» — PIN уже введён; нажатие позволяет его заменить.
        TextButton(onClick = onPin, enabled = enabled, modifier = Modifier.focusRing()) {
            Text(if (device.pin.isNullOrEmpty()) "PIN" else "PIN ✓")
        }
        TextButton(onClick = { SendState.remove(device.id) }, enabled = enabled, modifier = Modifier.focusRing()) { Text("✕") }
    }
}

/** Диалог с одним полем ввода (PIN устройства или его IP-адрес). */
@Composable
private fun TextDialog(
    title: String,
    message: String,
    label: String,
    initial: String,
    numeric: Boolean,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var value by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(message, style = MaterialTheme.typography.bodyMedium)
                OutlinedTextField(
                    value = value,
                    onValueChange = { value = if (numeric) it.filter(Char::isDigit).take(12) else it },
                    label = { Text(label) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = if (numeric) KeyboardType.Number else KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { if (value.isNotBlank()) onConfirm(value) }, enabled = value.isNotBlank()) {
                Text(stringResource(R.string.dialog_ok))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.dialog_cancel)) } },
    )
}
