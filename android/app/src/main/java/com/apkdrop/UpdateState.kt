package com.apkdrop

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import kotlin.concurrent.thread

/** Что показывать в окне обновления. */
sealed interface UpdateUi {
    data object None : UpdateUi
    class Available(val release: UpdateChecker.Release, val current: String) : UpdateUi
    class Downloading(val release: UpdateChecker.Release, val done: Long, val total: Long) : UpdateUi
    class Failed(val release: UpdateChecker.Release, val message: String) : UpdateUi
}

/** Проверка и установка новой версии ApkDrop из GitHub Releases. */
object UpdateState {
    private val _ui = MutableStateFlow<UpdateUi>(UpdateUi.None)
    val ui: StateFlow<UpdateUi> = _ui.asStateFlow()

    @Volatile
    private var checkedThisProcess = false

    @Volatile
    private var cancelled = false

    /** При открытии приложения: один раз за запуск процесса, тихо — без сети ничего не показываем. */
    fun checkOnStart(context: Context) {
        if (checkedThisProcess) return
        checkedThisProcess = true
        if (!Prefs(context).checkUpdates) return
        check(context, manual = false)
    }

    /** [manual] — по кнопке «Проверить сейчас»: пишет результат в журнал и не учитывает «Пропустить версию». */
    fun check(context: Context, manual: Boolean) {
        val app = context.applicationContext
        thread(name = "apkdrop-update-check") {
            try {
                val release = UpdateChecker.fetchLatest() ?: return@thread
                val current = currentVersion(app)
                val currentParts = UpdateChecker.parseVersion(current) ?: return@thread
                if (!UpdateChecker.isNewer(release.version, currentParts)) {
                    if (manual) DropState.log(app.getString(R.string.upd_log_latest, current), true)
                    return@thread
                }
                if (!manual && Prefs(app).skippedVersion == release.tag) return@thread
                if (_ui.value is UpdateUi.None) _ui.value = UpdateUi.Available(release, current)
            } catch (e: Exception) {
                if (manual) DropState.log(app.getString(R.string.upd_log_check_failed, e.message ?: e.javaClass.simpleName), false)
            }
        }
    }

    fun later() {
        cancelled = true
        _ui.value = UpdateUi.None
    }

    fun skip(context: Context, release: UpdateChecker.Release) {
        Prefs(context).skippedVersion = release.tag
        _ui.value = UpdateUi.None
    }

    /** Скачивает APK, проверяет его и отдаёт на установку тем же механизмом, которым ApkDrop ставит чужие приложения. */
    fun startUpdate(context: Context, release: UpdateChecker.Release) {
        val ctx = context // с языком интерфейса — для сообщений установщика
        val app = context.applicationContext
        cancelled = false
        _ui.value = UpdateUi.Downloading(release, 0, release.apkSize)
        thread(name = "apkdrop-update") {
            val dir = File(app.cacheDir, "updates").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
            val file = File(dir, "ApkDrop-${release.tag}.apk")
            try {
                if (!app.packageManager.canRequestPackageInstalls()) {
                    throw UpdateChecker.UpdateException(app.getString(R.string.upd_err_permission))
                }
                val errors = UpdateChecker.Errors(
                    noApk = app.getString(R.string.upd_err_no_apk),
                    noChecksum = app.getString(R.string.upd_err_no_checksum),
                    size = app.getString(R.string.upd_err_size),
                    checksum = app.getString(R.string.upd_err_checksum),
                )
                var lastPercent = -1
                UpdateChecker.download(release, file, errors, isCancelled = { cancelled }) { done, total ->
                    val percent = if (total > 0) (done * 100 / total).toInt() else 0
                    if (percent != lastPercent) {
                        lastPercent = percent
                        if (!cancelled) _ui.value = UpdateUi.Downloading(release, done, total)
                    }
                }

                // Это должен быть именно ApkDrop и версия новее установленной: ошибка в релизе не должна ставить чужое.
                @Suppress("DEPRECATION")
                val info = app.packageManager.getPackageArchiveInfo(file.path, 0)
                if (info == null || info.packageName != app.packageName) throw UpdateChecker.UpdateException(app.getString(R.string.upd_err_package))

                _ui.value = UpdateUi.None
                // Если установка удастся, система перезапустит процесс; при отказе причина попадёт в журнал.
                Installer.install(app, ctx, file, false) { }
            } catch (e: Exception) {
                if (cancelled) {
                    _ui.value = UpdateUi.None
                } else {
                    _ui.value = UpdateUi.Failed(release, e.message ?: e.javaClass.simpleName)
                }
            }
        }
    }

    fun dismissFailure() {
        _ui.value = UpdateUi.None
    }

    private fun currentVersion(context: Context): String =
        runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }.getOrNull().orEmpty()
}
