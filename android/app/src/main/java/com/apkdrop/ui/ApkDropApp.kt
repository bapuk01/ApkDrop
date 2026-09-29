package com.apkdrop.ui

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import androidx.compose.foundation.border
import androidx.compose.foundation.focusable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.apkdrop.AppLocale
import com.apkdrop.DropState
import com.apkdrop.HTTP_PORT
import com.apkdrop.Installer
import com.apkdrop.LogEntry
import com.apkdrop.Net
import com.apkdrop.Prefs
import com.apkdrop.R
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class Actions(
    val setServer: (Boolean) -> Unit,
    val openInstallSettings: () -> Unit,
    val requestNotifications: () -> Unit,
    val requestBattery: () -> Unit,
    val currentLanguage: () -> String,
    val setLanguage: (String) -> Unit,
)

private val okColor = Color(0xFF2E7D32)
private val errColor = Color(0xFFC62828)

@Composable
fun ApkDropApp(prefs: Prefs, tick: Int, actions: Actions) {
    val scheme = if (isSystemInDarkTheme()) {
        darkColorScheme(primary = Color(0xFF81C784))
    } else {
        lightColorScheme(primary = okColor)
    }
    MaterialTheme(colorScheme = scheme) {
        MainScreen(prefs, tick, actions)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MainScreen(prefs: Prefs, tick: Int, actions: Actions) {
    val context = LocalContext.current
    val running by DropState.running.collectAsState()
    val log by DropState.log.collectAsState()
    val addresses = remember(tick, running) { Net.localAddresses() }
    val perms = remember(tick) { Permissions.read(context) }
    var pin by remember { mutableStateOf(prefs.pin) }
    var autostart by remember { mutableStateOf(prefs.autostart) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("ApkDrop") },
                actions = { LanguageButton(actions) },
            )
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.padding(padding).fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(stringResource(R.string.ui_receive), style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                            Switch(checked = running, onCheckedChange = actions.setServer, modifier = Modifier.focusRing())
                        }
                        when {
                            !running -> Text(stringResource(R.string.ui_receive_off))
                            addresses.isEmpty() -> Text(stringResource(R.string.notif_no_wifi), color = errColor)
                            else -> addresses.forEach {
                                Text("$it:$HTTP_PORT", fontFamily = FontFamily.Monospace, fontSize = 18.sp)
                            }
                        }
                        HorizontalDivider(Modifier.padding(vertical = 6.dp))
                        Text(stringResource(R.string.ui_pin_label), style = MaterialTheme.typography.labelLarge)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                pin.chunked(3).joinToString(" "),
                                fontFamily = FontFamily.Monospace,
                                fontWeight = FontWeight.Bold,
                                fontSize = 32.sp,
                                modifier = Modifier.weight(1f),
                            )
                            TextButton(onClick = { pin = prefs.newPin() }, modifier = Modifier.focusRing()) { Text(stringResource(R.string.ui_change)) }
                        }
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(stringResource(R.string.ui_setup), style = MaterialTheme.typography.titleMedium)
                        PermissionRow(
                            title = stringResource(R.string.perm_install_title),
                            hint = stringResource(R.string.perm_install_hint),
                            ok = perms.canInstall,
                            onFix = actions.openInstallSettings,
                        )
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                            PermissionRow(
                                title = stringResource(R.string.perm_notif_title),
                                hint = stringResource(R.string.perm_notif_hint),
                                ok = perms.notifications,
                                onFix = actions.requestNotifications,
                            )
                        }
                        PermissionRow(
                            title = stringResource(R.string.perm_battery_title),
                            hint = stringResource(R.string.perm_battery_hint),
                            ok = perms.battery,
                            onFix = actions.requestBattery,
                        )
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(stringResource(R.string.autostart_title))
                                Text(stringResource(R.string.autostart_hint), style = MaterialTheme.typography.bodySmall)
                            }
                            Switch(
                                checked = autostart,
                                onCheckedChange = { autostart = it; prefs.autostart = it },
                                modifier = Modifier.focusRing(),
                            )
                        }
                        Spacer(Modifier.padding(2.dp))
                        Text(
                            stringResource(if (Installer.silentUpdatesSupported) R.string.silent_yes else R.string.silent_no),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }

            item {
                Text(stringResource(R.string.ui_log), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 4.dp))
            }
            if (log.isEmpty()) {
                item { Text(stringResource(R.string.ui_log_empty)) }
            }
            items(log) { LogRow(it) }

            item {
                val version = remember {
                    runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }.getOrNull()
                }
                Text(
                    "ApkDrop ${version.orEmpty()} · © $AUTHOR",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(top = 20.dp, bottom = 8.dp),
                )
            }
        }
    }
}

private const val AUTHOR = "Bapuk01"

/** Значок-глобус в верхней панели: меню «Как в системе / English / Русский». */
@Composable
private fun LanguageButton(actions: Actions) {
    var open by remember { mutableStateOf(false) }
    val current = remember(open) { actions.currentLanguage() }
    Box {
        IconButton(onClick = { open = true }, modifier = Modifier.focusRing()) {
            Icon(painterResource(R.drawable.ic_language), contentDescription = stringResource(R.string.ui_language))
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            AppLocale.choices.forEach { language ->
                // Названия языков — на самих этих языках, чтобы их можно было найти в любом интерфейсе.
                val name = when (language) {
                    "en" -> "English"
                    "ru" -> "Русский"
                    else -> stringResource(R.string.ui_language_system)
                }
                DropdownMenuItem(
                    text = { Text(name, fontWeight = if (language == current) FontWeight.Bold else FontWeight.Normal) },
                    trailingIcon = { if (language == current) Text("✓", color = MaterialTheme.colorScheme.primary) },
                    onClick = {
                        open = false
                        if (language != current) actions.setLanguage(language)
                    },
                )
            }
        }
    }
}

/**
 * Заметная рамка вокруг элемента в фокусе — на Android TV управление идёт пультом,
 * а стандартная подсветка Material почти не видна с дивана.
 */
@Composable
private fun Modifier.focusRing(shape: Shape = RoundedCornerShape(50)): Modifier {
    var focused by remember { mutableStateOf(false) }
    return this
        .onFocusChanged { focused = it.hasFocus }
        .border(3.dp, if (focused) MaterialTheme.colorScheme.primary else Color.Transparent, shape)
}

@Composable
private fun PermissionRow(title: String, hint: String, ok: Boolean, onFix: () -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(vertical = 2.dp)) {
        Text(if (ok) "✓" else "✗", color = if (ok) okColor else errColor, fontWeight = FontWeight.Bold)
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(title)
            Text(hint, style = MaterialTheme.typography.bodySmall)
        }
        if (!ok) TextButton(onClick = onFix, modifier = Modifier.focusRing()) { Text(stringResource(R.string.ui_allow)) }
    }
}

private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.getDefault())

@Composable
private fun LogRow(entry: LogEntry) {
    // focusable — чтобы журнал можно было пролистать пультом.
    Row(Modifier.fillMaxWidth().focusRing(RoundedCornerShape(6.dp)).focusable().padding(4.dp)) {
        Text(timeFormat.format(Date(entry.time)), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
        Spacer(Modifier.width(8.dp))
        Text(
            entry.text,
            style = MaterialTheme.typography.bodyMedium,
            color = when (entry.ok) {
                true -> okColor
                false -> errColor
                null -> Color.Unspecified
            },
        )
    }
}

private data class Permissions(
    val canInstall: Boolean,
    val notifications: Boolean,
    val battery: Boolean,
) {
    companion object {
        fun read(context: Context) = Permissions(
            canInstall = context.packageManager.canRequestPackageInstalls(),
            notifications = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED,
            battery = context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(context.packageName),
        )
    }
}
