package com.apkdrop

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.pm.PackageInfoCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** Другое устройство с ApkDrop, на которое можно отправлять APK. [status] и [statusOk] только для экрана, на диск не пишутся. */
data class Remote(
    val id: String,
    val name: String,
    val model: String,
    val host: String,
    val port: Int,
    val pin: String? = null,
    val checked: Boolean = true,
    val status: String = "",
    val statusOk: Boolean? = null,
)

/** APK, выбранный для отправки: копия во временной папке приложения плюс то, что удалось прочитать из файла. */
data class SelectedApk(
    val file: File,
    val fileName: String,
    val pkg: String,
    val label: String,
    val versionName: String,
    val versionCode: Long,
    val size: Long,
)

/** Состояние вкладки «Отправить»: живёт в процессе, поэтому не теряется при повороте экрана и смене языка. */
object SendState {
    private lateinit var app: Context
    private var loaded = false

    private val _devices = MutableStateFlow<List<Remote>>(emptyList())
    val devices: StateFlow<List<Remote>> = _devices.asStateFlow()

    private val _apk = MutableStateFlow<SelectedApk?>(null)
    val apk: StateFlow<SelectedApk?> = _apk.asStateFlow()

    val busy = MutableStateFlow(false)
    val progress = MutableStateFlow(0f)

    private val _log = MutableStateFlow<List<LogEntry>>(emptyList())
    val log: StateFlow<List<LogEntry>> = _log.asStateFlow()

    /** Запрос открыть вкладку «Отправить» (после «Открыть с помощью ApkDrop» в файловом менеджере). */
    val openSendTab = MutableStateFlow(false)

    @Volatile
    var autoDiscovered = false

    private val prefs get() = app.getSharedPreferences("apkdrop_sender", Context.MODE_PRIVATE)

    fun init(context: Context) {
        if (loaded) return
        app = context.applicationContext
        loaded = true
        runCatching {
            val arr = JSONArray(prefs.getString("devices", "[]"))
            _devices.value = (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                Remote(
                    id = o.getString("id"), name = o.optString("name"), model = o.optString("model"),
                    host = o.getString("host"), port = o.optInt("port", HTTP_PORT),
                    pin = o.optString("pin").takeIf { it.isNotEmpty() }, checked = o.optBoolean("checked", true),
                )
            }
        }
        // Выбранный APK переживает перезапуск процесса (Android часто выгружает приложения из фона).
        runCatching {
            val path = prefs.getString("apk_path", null)
            val name = prefs.getString("apk_name", null)
            if (path != null && name != null && File(path).exists()) _apk.value = inspect(File(path), name)
        }
    }

    var launchAfter: Boolean
        get() = prefs.getBoolean("launch_after", true)
        set(value) = prefs.edit().putBoolean("launch_after", value).apply()

    fun log(text: String, ok: Boolean? = null) {
        _log.update { (listOf(LogEntry(System.currentTimeMillis(), text, ok)) + it).take(100) }
    }

    fun tryBegin(): Boolean = busy.compareAndSet(false, true)

    // ---------- Устройства ----------

    private fun persist() {
        val arr = JSONArray()
        for (d in _devices.value) {
            arr.put(
                JSONObject().put("id", d.id).put("name", d.name).put("model", d.model)
                    .put("host", d.host).put("port", d.port).put("pin", d.pin ?: "").put("checked", d.checked)
            )
        }
        prefs.edit().putString("devices", arr.toString()).apply()
    }

    fun device(id: String): Remote? = _devices.value.firstOrNull { it.id == id }

    /** Добавляет устройство или обновляет адрес и название уже известного (PIN и галочка сохраняются). */
    fun upsert(info: JSONObject, host: String, portOverride: Int? = null): Remote {
        val id = info.getString("id")
        val port = portOverride ?: info.optInt("port", HTTP_PORT)
        val canInstall = info.optBoolean("canInstall", true)
        val status = if (canInstall) app.getString(R.string.st_online) else app.getString(R.string.st_no_permission)
        var result: Remote? = null
        _devices.update { list ->
            val old = list.firstOrNull { it.id == id }
            // Если на том же адресе ApkDrop переустановили — у него новый id; старую запись заменяем, PIN при этом теряется.
            val base = list.filter { it.id != id && !(it.host == host && it.model == info.optString("model")) }
            val merged = (old ?: Remote(id, "", "", host, port)).copy(
                name = info.optString("name"), model = info.optString("model"), host = host, port = port,
                status = status, statusOk = if (canInstall) true else null,
            )
            result = merged
            base + merged
        }
        persist()
        return result!!
    }

    /** Устройства, которых нет в ответах на поиск, помечаются «не найден». */
    fun markMissing(foundIds: Set<String>) {
        _devices.update { list ->
            list.map { if (it.id in foundIds) it else it.copy(status = app.getString(R.string.st_not_found), statusOk = false) }
        }
    }

    fun setStatus(id: String, text: String, ok: Boolean? = null) {
        _devices.update { list -> list.map { if (it.id == id) it.copy(status = text, statusOk = ok) else it } }
    }

    fun setChecked(id: String, checked: Boolean) {
        _devices.update { list -> list.map { if (it.id == id) it.copy(checked = checked) else it } }
        persist()
    }

    fun setPin(id: String, pin: String?) {
        _devices.update { list -> list.map { if (it.id == id) it.copy(pin = pin?.filter { c -> c.isLetterOrDigit() }?.ifEmpty { null }) else it } }
        persist()
    }

    fun remove(id: String) {
        _devices.update { list -> list.filter { it.id != id } }
        persist()
    }

    fun updateAddress(id: String, host: String) {
        _devices.update { list -> list.map { if (it.id == id) it.copy(host = host) else it } }
        persist()
    }

    // ---------- Выбранный APK ----------

    /** Копирует выбранный файл во временную папку и читает из него название и версию. Вызывать не из главного потока. */
    fun importApk(context: Context, uri: Uri) {
        init(context)
        if (busy.value) {
            log(app.getString(R.string.send_apk_busy), false)
            return
        }
        val dir = File(app.cacheDir, "outgoing").apply { mkdirs() }
        val target = File(dir, "selected-${System.currentTimeMillis()}.apk")
        val fileName = displayName(uri) ?: "app.apk"
        try {
            val input = app.contentResolver.openInputStream(uri) ?: throw IllegalStateException("no stream")
            input.use { src -> target.outputStream().use { src.copyTo(it, 256 * 1024) } }
        } catch (e: Exception) {
            target.delete()
            log(app.getString(R.string.send_apk_copy_failed, e.message ?: e.javaClass.simpleName), false)
            return
        }
        val selected = inspect(target, fileName)
        if (selected == null) {
            target.delete()
            log(app.getString(R.string.send_apk_invalid), false)
            return
        }
        _apk.value = selected
        prefs.edit().putString("apk_path", target.path).putString("apk_name", fileName).apply()
        // Прежние копии больше не нужны.
        dir.listFiles()?.filter { it != target }?.forEach { it.delete() }
    }

    @Suppress("DEPRECATION")
    private fun inspect(file: File, fileName: String): SelectedApk? {
        val pm = app.packageManager
        val info = pm.getPackageArchiveInfo(file.path, 0) ?: return null
        val label = info.applicationInfo?.let {
            it.sourceDir = file.path
            it.publicSourceDir = file.path
            pm.getApplicationLabel(it).toString()
        } ?: info.packageName
        return SelectedApk(
            file = file, fileName = fileName, pkg = info.packageName, label = label,
            versionName = info.versionName.orEmpty(), versionCode = PackageInfoCompat.getLongVersionCode(info), size = file.length(),
        )
    }

    private fun displayName(uri: Uri): String? = runCatching {
        app.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        }
    }.getOrNull() ?: uri.lastPathSegment
}
