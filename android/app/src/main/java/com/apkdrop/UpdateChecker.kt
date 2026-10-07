package com.apkdrop

import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Поиск новой версии ApkDrop в GitHub Releases. В релизе нужен файл *.apk; тег — «1.8» или «v1.8».
 * Скачанный файл принимается только при совпадении размера и SHA-256, которые публикует GitHub.
 */
object UpdateChecker {
    private const val API = "https://api.github.com/repos/bapuk01/ApkDrop/releases/latest"
    const val RELEASES_URL = "https://github.com/bapuk01/ApkDrop/releases"

    class Release(
        val tag: String,
        val version: List<Int>,
        val notes: String,
        val pageUrl: String,
        val apkUrl: String?,
        val apkSize: Long,
        val apkSha256: String?,
    ) {
        /** «1.8» — для показа пользователю. */
        val versionText: String get() = version.joinToString(".")
    }

    /** Сообщение об ошибке, которое уже можно показать пользователю. */
    class UpdateException(message: String) : IOException(message)

    /** «v1.8», «1.8.0», «release-1.9-beta» → [1, 8] … Всё, кроме первой группы цифр с точками, отбрасывается. */
    fun parseVersion(text: String?): List<Int>? {
        val match = Regex("""\d+(\.\d+)*""").find(text.orEmpty()) ?: return null
        val parts = match.value.split('.').map { it.toIntOrNull() ?: return null }
        return parts
    }

    /** Версии сравниваются по числам («1.10» новее «1.9»); недостающие части считаются нулями («1.8» = «1.8.0»). */
    fun isNewer(latest: List<Int>, current: List<Int>): Boolean {
        for (i in 0 until maxOf(latest.size, current.size)) {
            val a = latest.getOrElse(i) { 0 }
            val b = current.getOrElse(i) { 0 }
            if (a != b) return a > b
        }
        return false
    }

    /** Последний опубликованный релиз; null — релизов нет. Блокирующий вызов, не из главного потока. */
    fun fetchLatest(): Release? {
        val conn = open(API, readTimeoutMs = 20_000)
        try {
            if (conn.responseCode == 404) return null
            if (conn.responseCode != 200) throw UpdateException("HTTP ${conn.responseCode}")
            val json = org.json.JSONObject(conn.inputStream.bufferedReader().use { it.readText() })
            val tag = json.optString("tag_name")
            val version = parseVersion(tag) ?: return null

            var apkUrl: String? = null
            var apkSize = 0L
            var apkSha: String? = null
            val assets = json.optJSONArray("assets")
            for (i in 0 until (assets?.length() ?: 0)) {
                val asset = assets!!.getJSONObject(i)
                if (!asset.optString("name").endsWith(".apk", ignoreCase = true)) continue
                apkUrl = asset.optString("browser_download_url").ifEmpty { null }
                apkSize = asset.optLong("size")
                apkSha = asset.optString("digest").takeIf { it.startsWith("sha256:", ignoreCase = true) }?.substring(7)?.lowercase()
                break
            }
            return Release(tag, version, json.optString("body"), json.optString("html_url").ifEmpty { RELEASES_URL }, apkUrl, apkSize, apkSha)
        } finally {
            conn.disconnect()
        }
    }

    /**
     * Скачивает APK в [target]. [isCancelled] проверяется между блоками. При любой ошибке частичный файл удаляется.
     * Тексты ошибок берутся из [errors]: так они на языке интерфейса.
     */
    fun download(release: Release, target: File, errors: Errors, isCancelled: () -> Boolean, onProgress: (Long, Long) -> Unit) {
        val url = release.apkUrl ?: throw UpdateException(errors.noApk)
        val expected = release.apkSha256 ?: throw UpdateException(errors.noChecksum)
        val conn = open(url, readTimeoutMs = 30_000)
        try {
            if (conn.responseCode != 200) throw UpdateException("HTTP ${conn.responseCode}")
            val total = conn.contentLengthLong.takeIf { it > 0 } ?: release.apkSize
            val digest = MessageDigest.getInstance("SHA-256")
            var done = 0L
            conn.inputStream.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(128 * 1024)
                    while (true) {
                        if (isCancelled()) throw UpdateException("cancelled")
                        val n = input.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        digest.update(buffer, 0, n)
                        done += n
                        onProgress(done, total)
                    }
                }
            }
            if (release.apkSize > 0 && done != release.apkSize) throw UpdateException(errors.size)
            val actual = digest.digest().joinToString("") { "%02x".format(it) }
            if (!actual.equals(expected, ignoreCase = true)) throw UpdateException(errors.checksum)
        } catch (e: Exception) {
            target.delete()
            throw e
        } finally {
            conn.disconnect()
        }
    }

    class Errors(val noApk: String, val noChecksum: String, val size: String, val checksum: String)

    private fun open(url: String, readTimeoutMs: Int): HttpURLConnection {
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = 15_000
        conn.readTimeout = readTimeoutMs
        conn.setRequestProperty("User-Agent", "ApkDrop-updater/1.0")
        conn.setRequestProperty("Accept", "application/vnd.github+json")
        return conn
    }

    /**
     * Описание релиза без разметки Markdown — для окна обновления. Если в описании есть линия «---»,
     * то до неё — русский текст, после — краткий английский: каждому показываем свой.
     */
    fun plainNotes(markdown: String, english: Boolean): String {
        val parts = markdown.replace("\r", "").split(Regex("(?m)^[ \\t]*-{3,}[ \\t]*$"))
        val text = if (english && parts.size > 1 && parts[1].isNotBlank()) parts[1] else parts[0]

        val lines = ArrayList<String>()
        for (raw in text.split('\n')) {
            var line = raw.trim()
            line = line.replace(Regex("^#{1,6}\\s*"), "")
                .replace(Regex("^[-*]\\s+"), "• ")
                .replace(Regex("\\[([^\\]]+)\\]\\([^)]+\\)"), "$1")
                .replace("**", "").replace("`", "")
            if (line.isEmpty() && (lines.isEmpty() || lines.last().isEmpty())) continue
            lines.add(line)
        }
        return lines.joinToString("\n").trim()
    }
}
