package com.apkdrop

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.core.content.IntentCompat
import androidx.core.content.pm.PackageInfoCompat
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Установка APK через PackageInstaller.
 *
 * На Android 12+ сессия создаётся с USER_ACTION_NOT_REQUIRED: если приложение уже было
 * установлено через ApkDrop, система обновит его молча. Первая установка (или обновление
 * приложения, поставленного кем-то другим) всегда требует одного нажатия «Установить».
 */
object Installer {
    private val results = ConcurrentHashMap<Int, LinkedBlockingQueue<Intent>>()
    private val installLock = Any()

    /** Сколько ждём, пока пользователь нажмёт «Установить» на телефоне. */
    private const val CONFIRM_TIMEOUT_MIN = 5L

    /** Вызывается из InstallResultReceiver. */
    fun onResult(intent: Intent) {
        val id = intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1)
        results[id]?.offer(intent)
    }

    val silentUpdatesSupported: Boolean
        get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S

    /** Android не даёт открывать окна из фона, кроме как видимой activity или с разрешением «поверх других приложений». */
    fun canPopUp(context: Context): Boolean =
        DropState.activityVisible || Settings.canDrawOverlays(context)

    /**
     * Блокирующая установка. Промежуточные события отдаются в [emit],
     * возвращается финальное событие {"event":"done","ok":...}.
     * [texts] — строки на языке ПК-клиента (см. [Texts]); журнал телефона пишется на языке телефона.
     */
    fun install(context: Context, texts: Context, apk: File, launch: Boolean, emit: (JSONObject) -> Unit): JSONObject =
        synchronized(installLock) {
            try {
                doInstall(context, texts, apk, launch, emit)
            } catch (e: Exception) {
                done(false, texts.getString(R.string.install_error, e.message ?: e.javaClass.simpleName))
            }
        }

    @Suppress("DEPRECATION")
    private fun doInstall(context: Context, texts: Context, apk: File, launch: Boolean, emit: (JSONObject) -> Unit): JSONObject {
        val pm = context.packageManager
        val info = pm.getPackageArchiveInfo(apk.path, 0)
            ?: return done(false, texts.getString(R.string.not_apk))
        val pkg = info.packageName
        val label = info.applicationInfo?.let {
            it.sourceDir = apk.path
            it.publicSourceDir = apk.path
            pm.getApplicationLabel(it).toString()
        } ?: pkg
        val installed: PackageInfo? = try {
            pm.getPackageInfo(pkg, 0)
        } catch (e: PackageManager.NameNotFoundException) {
            null
        }

        emit(
            JSONObject()
                .put("event", "parsed")
                .put("package", pkg)
                .put("label", label)
                .put("version", info.versionName ?: "")
                .put("versionCode", PackageInfoCompat.getLongVersionCode(info))
                .put("installedVersion", installed?.versionName ?: JSONObject.NULL)
                .put("installedVersionCode", installed?.let { PackageInfoCompat.getLongVersionCode(it) } ?: JSONObject.NULL)
                .put("installer", installed?.let { installerOf(pm, pkg) } ?: JSONObject.NULL)
        )
        val what = "$label ${info.versionName ?: ""}".trim()
        DropState.log(context.getString(R.string.log_received, what, apk.length() / 1024))

        if (pkg == context.packageName) {
            // Процесс будет убит системой после обновления — ответ до ПК может не дойти.
            emit(JSONObject().put("event", "self-update"))
        }

        val installer = pm.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(pkg)
            setSize(apk.length())
            setInstallReason(PackageManager.INSTALL_REASON_USER)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            }
        }
        val sessionId = installer.createSession(params)
        val queue = LinkedBlockingQueue<Intent>()
        results[sessionId] = queue
        val notifyId = 1000 + sessionId % 1000
        val packageAdded = if (launch) PackageAddedWaiter(context, pkg) else null
        try {
            installer.openSession(sessionId).use { session ->
                session.openWrite("base.apk", 0, apk.length()).use { out ->
                    apk.inputStream().use { it.copyTo(out, 256 * 1024) }
                    session.fsync(out)
                }
                val callback = Intent(context, InstallResultReceiver::class.java).setPackage(context.packageName)
                var flags = PendingIntent.FLAG_UPDATE_CURRENT
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) flags = flags or PendingIntent.FLAG_MUTABLE
                val pi = PendingIntent.getBroadcast(context, sessionId, callback, flags)
                emit(JSONObject().put("event", "installing"))
                session.commit(pi.intentSender)
            }

            while (true) {
                val result = queue.poll(CONFIRM_TIMEOUT_MIN, TimeUnit.MINUTES)
                    ?: return done(false, texts.getString(R.string.confirm_timeout, CONFIRM_TIMEOUT_MIN))
                val status = result.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
                val message = result.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                when (status) {
                    PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                        val confirm = IntentCompat.getParcelableExtra(result, Intent.EXTRA_INTENT, Intent::class.java)
                            ?: return done(false, texts.getString(R.string.no_confirm_window))
                        val popup = canPopUp(context)
                        askUser(context, confirm, what, notifyId)
                        emit(
                            JSONObject().put("event", "confirm")
                                .put("message", texts.getString(R.string.confirm_on_phone))
                                .put("popup", popup)
                        )
                    }
                    PackageInstaller.STATUS_SUCCESS -> {
                        val verb = if (installed == null) R.string.installed_fmt else R.string.updated_fmt
                        DropState.log(context.getString(verb, what), ok = true)
                        // STATUS_SUCCESS приходит раньше, чем система закрывает старые задачи обновлённого
                        // приложения, — если запустить сразу, свежая activity будет тут же убита.
                        packageAdded?.await()
                        val result = done(true, texts.getString(verb, what)).put("package", pkg)
                        if (launch) {
                            val error = launchApp(context, texts, pkg)
                            result.put("launched", error == null)
                            if (error != null) result.put("launchError", error)
                        }
                        return result
                    }
                    else -> {
                        DropState.log("$what — ${describeFailure(context, status, message)}", ok = false)
                        return done(false, describeFailure(texts, status, message)).put("package", pkg).put("status", status)
                    }
                }
            }
        } catch (e: Exception) {
            runCatching { installer.abandonSession(sessionId) }
            throw e
        } finally {
            packageAdded?.close()
            results.remove(sessionId)
            context.getSystemService(NotificationManager::class.java).cancel(notifyId)
        }
    }

    private fun done(ok: Boolean, message: String) =
        JSONObject().put("event", "done").put("ok", ok).put("message", message)

    /** Установленные версии запрошенных пакетов: {"packages": {"pkg": {...} | null}}. */
    fun installedVersions(context: Context, packages: List<String>): JSONObject {
        val pm = context.packageManager
        val result = JSONObject()
        for (pkg in packages) {
            val info = try {
                pm.getPackageInfo(pkg, 0)
            } catch (e: PackageManager.NameNotFoundException) {
                null
            }
            result.put(
                pkg,
                info?.let {
                    JSONObject()
                        .put("versionCode", PackageInfoCompat.getLongVersionCode(it))
                        .put("versionName", it.versionName ?: "")
                        .put("label", it.applicationInfo?.loadLabel(pm)?.toString() ?: pkg)
                        .put("installer", installerOf(pm, pkg) ?: JSONObject.NULL)
                        .put("sha256", Fingerprint.apkSha256(it) ?: JSONObject.NULL)
                        .put("lastUpdateTime", it.lastUpdateTime)
                        .put("certs", JSONArray(Fingerprint.signerDigests(pm, pkg)))
                } ?: JSONObject.NULL,
            )
        }
        return JSONObject().put("packages", result)
    }

    @Suppress("DEPRECATION")
    private fun installerOf(pm: PackageManager, pkg: String): String? = try {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            pm.getInstallSourceInfo(pkg).installingPackageName
        } else {
            pm.getInstallerPackageName(pkg)
        }
    } catch (e: Exception) {
        null
    }

    /**
     * Показывает системный диалог «Установить?». Если ApkDrop сейчас на экране — открываем сразу,
     * иначе Android не даст стартовать activity из фона, поэтому дублируем уведомлением.
     */
    private fun askUser(context: Context, confirm: Intent, what: String, notifyId: Int) {
        confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        runCatching { context.startActivity(confirm) }

        val pi = PendingIntent.getActivity(
            context, notifyId, confirm,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(context, DropService.CHANNEL_CONFIRM)
            .setSmallIcon(R.drawable.ic_notify)
            .setContentTitle(context.getString(R.string.notif_confirm_title))
            .setContentText(what)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setContentIntent(pi)
            .setAutoCancel(true)
            .build()
        runCatching { context.getSystemService(NotificationManager::class.java).notify(notifyId, notification) }
    }

    /** Ждёт системного PACKAGE_ADDED для пакета — к этому моменту система закончила чистку старых задач. */
    private class PackageAddedWaiter(context: Context, private val pkg: String) : BroadcastReceiver() {
        private val app = context.applicationContext
        private val latch = CountDownLatch(1)

        init {
            val filter = IntentFilter(Intent.ACTION_PACKAGE_ADDED).apply { addDataScheme("package") }
            ContextCompat.registerReceiver(app, this, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        }

        override fun onReceive(context: Context, intent: Intent) {
            if (intent.data?.schemeSpecificPart == pkg) latch.countDown()
        }

        fun await() {
            latch.await(5, TimeUnit.SECONDS)
            Thread.sleep(300)
        }

        fun close() {
            runCatching { app.unregisterReceiver(this) }
        }
    }

    /** Возвращает null при успехе или причину, по которой приложение не запущено. */
    private fun launchApp(context: Context, texts: Context, pkg: String): String? {
        if (pkg == context.packageName) return texts.getString(R.string.launch_self)
        val pm = context.packageManager
        // У приложений только для Android TV нет обычного значка — есть только LEANBACK_LAUNCHER.
        val intent = pm.getLaunchIntentForPackage(pkg)
            ?: pm.getLeanbackLaunchIntentForPackage(pkg)
            ?: return texts.getString(R.string.launch_no_icon)
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        val allowed = canPopUp(context)
        // Пробуем в любом случае: некоторые прошивки пропускают запуск из фона.
        val error = runCatching { context.startActivity(intent) }.exceptionOrNull()
        return when {
            error != null -> error.message ?: error.javaClass.simpleName
            !allowed -> texts.getString(R.string.launch_blocked)
            else -> null
        }
    }

    private fun describeFailure(texts: Context, status: Int, message: String?): String {
        val raw = message.orEmpty()
        val hint = when {
            "UPDATE_INCOMPATIBLE" in raw || "INCONSISTENT_CERTIFICATES" in raw -> R.string.fail_signature
            "VERSION_DOWNGRADE" in raw -> R.string.fail_downgrade
            "INSUFFICIENT_STORAGE" in raw -> R.string.fail_storage
            "OLDER_SDK" in raw -> R.string.fail_older_sdk
            "NO_MATCHING_ABIS" in raw -> R.string.fail_abi
            "TEST_ONLY" in raw -> R.string.fail_test_only
            status == PackageInstaller.STATUS_FAILURE_ABORTED -> R.string.fail_aborted
            status == PackageInstaller.STATUS_FAILURE_BLOCKED -> R.string.fail_blocked
            status == PackageInstaller.STATUS_FAILURE_CONFLICT -> R.string.fail_conflict
            status == PackageInstaller.STATUS_FAILURE_INCOMPATIBLE -> R.string.fail_incompatible
            status == PackageInstaller.STATUS_FAILURE_INVALID -> R.string.fail_invalid
            status == PackageInstaller.STATUS_FAILURE_STORAGE -> R.string.fail_storage_error
            else -> R.string.fail_generic
        }.let { texts.getString(it) }
        return if (raw.isNotEmpty()) "$hint ($raw)" else hint
    }
}
