package com.apkdrop

import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/**
 * «Отпечатки» установленного приложения для ПК:
 * SHA-256 файла base.apk — чтобы отличить новую сборку с тем же versionCode,
 * SHA-256 сертификатов подписи — чтобы выбрать сборку, которая встанет поверх установленной.
 */
object Fingerprint {
    private data class Key(val path: String, val length: Long, val modified: Long)

    private val hashCache = ConcurrentHashMap<Key, String>()

    /** SHA-256 установленного APK; null, если приложение из нескольких частей (split APK) или файл недоступен. */
    fun apkSha256(info: PackageInfo): String? {
        val app = info.applicationInfo ?: return null
        if (!app.splitSourceDirs.isNullOrEmpty()) return null
        val file = File(app.sourceDir ?: return null)
        if (!file.canRead()) return null
        val key = Key(file.path, file.length(), file.lastModified())
        hashCache[key]?.let { return it }
        return runCatching { sha256(file) }.getOrNull()?.also {
            hashCache.keys.removeAll { k -> k.path == key.path }
            hashCache[key] = it
        }
    }

    @Suppress("DEPRECATION")
    fun signerDigests(pm: PackageManager, pkg: String): List<String> = runCatching {
        val signatures = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val signing = pm.getPackageInfo(pkg, PackageManager.GET_SIGNING_CERTIFICATES).signingInfo ?: return emptyList()
            if (signing.hasMultipleSigners()) signing.apkContentsSigners else signing.signingCertificateHistory
        } else {
            pm.getPackageInfo(pkg, PackageManager.GET_SIGNATURES).signatures
        }
        signatures.orEmpty().map { hex(MessageDigest.getInstance("SHA-256").digest(it.toByteArray())) }.distinct()
    }.getOrDefault(emptyList())

    private fun sha256(file: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buf = ByteArray(256 * 1024)
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                md.update(buf, 0, n)
            }
        }
        return hex(md.digest())
    }

    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }
}
