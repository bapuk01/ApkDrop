package com.apkdrop

import android.content.Context
import android.os.SystemClock
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.EOFException
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.security.MessageDigest
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

/**
 * Минимальный HTTP/1.1 сервер без зависимостей.
 *
 *   GET  /info                 — информация о телефоне (без PIN)
 *   GET  /auth                 — проверка PIN
 *   GET  /packages?names=a,b   — установленные версии этих пакетов
 *   POST /install[?launch=1]   — тело запроса = APK; ответ — NDJSON-поток событий установки
 *
 * PIN передаётся заголовком X-Pin (или ?pin=). Пример без ПК-приложения:
 *   curl -T app.apk -H "X-Pin: 123456" http://192.168.1.5:8765/install
 */
class HttpServer(private val context: Context) {
    private val prefs = Prefs(context)
    private val pool = Executors.newCachedThreadPool()
    private var server: ServerSocket? = null

    private val failedAuth = AtomicInteger()
    @Volatile
    private var lockedUntil = 0L

    private class Request(
        val method: String,
        val path: String,
        val query: Map<String, String>,
        val headers: Map<String, String>,
    )

    /** Тексты для ПК — на языке из Accept-Language (так журнал ПК-клиента не смешивает языки). */
    private fun Request.texts(): Context = Texts.forLanguage(context, headers["accept-language"])

    private class Reject(val code: Int, message: String) : Exception(message)

    fun start() {
        val socket = ServerSocket()
        socket.reuseAddress = true
        socket.bind(InetSocketAddress(HTTP_PORT))
        server = socket
        thread(name = "apkdrop-http", isDaemon = true) {
            while (!socket.isClosed) {
                val client = try {
                    socket.accept()
                } catch (e: IOException) {
                    break
                }
                pool.execute { handle(client) }
            }
        }
    }

    fun stop() {
        runCatching { server?.close() }
        pool.shutdownNow()
    }

    private fun handle(socket: Socket) = socket.use {
        try {
            socket.soTimeout = 60_000
            socket.tcpNoDelay = true
            val input = BufferedInputStream(socket.getInputStream(), 64 * 1024)
            val out = BufferedOutputStream(socket.getOutputStream(), 16 * 1024)
            val req = readRequest(input) ?: return@use
            try {
                route(req, input, out)
            } catch (e: Reject) {
                respond(out, e.code, JSONObject().put("ok", false).put("message", e.message))
            }
            out.flush()
        } catch (e: IOException) {
            // Клиент отключился — ничего не делаем.
        }
    }

    private fun route(req: Request, input: InputStream, out: OutputStream) {
        when {
            req.method == "GET" && req.path == "/info" ->
                respond(out, 200, Net.info(context))

            req.method == "GET" && req.path == "/auth" -> {
                checkPin(req)
                respond(out, 200, JSONObject().put("ok", true))
            }

            req.method == "GET" && req.path == "/packages" -> {
                checkPin(req)
                val names = req.query["names"].orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }
                respond(out, 200, Installer.installedVersions(context, names))
            }

            (req.method == "POST" || req.method == "PUT") && req.path.startsWith("/install") ->
                install(req, input, out)

            req.method == "GET" && req.path == "/" ->
                respond(out, 200, Net.info(context).put("usage", "curl -T app.apk -H \"X-Pin: PIN\" http://<ip>:$HTTP_PORT/install"))

            else -> throw Reject(404, req.texts().getString(R.string.err_not_found, "${req.method} ${req.path}"))
        }
    }

    private fun checkPin(req: Request) {
        val texts = req.texts()
        val now = SystemClock.elapsedRealtime()
        if (now < lockedUntil) throw Reject(429, texts.getString(R.string.err_pin_locked))
        // Запрос без PIN — просто проверка связи, в неудачные попытки не засчитываем.
        val pin = (req.headers["x-pin"] ?: req.query["pin"]) ?: throw Reject(401, texts.getString(R.string.err_pin_required))
        if (MessageDigest.isEqual(pin.trim().toByteArray(), prefs.pin.toByteArray())) {
            failedAuth.set(0)
            return
        }
        if (failedAuth.incrementAndGet() >= 5) {
            failedAuth.set(0)
            lockedUntil = now + 60_000
            DropState.log(context.getString(R.string.log_pin_locked), ok = false)
        }
        throw Reject(401, texts.getString(R.string.err_pin_wrong))
    }

    private fun install(req: Request, input: InputStream, out: OutputStream) {
        checkPin(req)
        val texts = req.texts()
        val length = req.headers["content-length"]?.toLongOrNull()
            ?: throw Reject(411, texts.getString(R.string.err_length_required))
        if (length <= 0) throw Reject(400, texts.getString(R.string.err_empty_file))
        val dir = File(context.cacheDir, "incoming").apply { mkdirs() }
        if (dir.usableSpace < length * 3) throw Reject(507, texts.getString(R.string.err_no_space))

        if (req.headers["expect"].equals("100-continue", ignoreCase = true)) {
            out.write("HTTP/1.1 100 Continue\r\n\r\n".toByteArray())
            out.flush()
        }

        val launch = req.query["launch"].let { it == "1" || it == "true" }
        val file = File.createTempFile("upload", ".apk", dir)
        try {
            file.outputStream().use { copyExactly(input, it, length) }

            out.write(
                ("HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/x-ndjson; charset=utf-8\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: close\r\n\r\n").toByteArray()
            )
            val emit = { obj: JSONObject ->
                // Если ПК отвалился — установку всё равно доводим до конца.
                runCatching {
                    out.write((obj.toString() + "\n").toByteArray(Charsets.UTF_8))
                    out.flush()
                }
                Unit
            }
            emit(JSONObject().put("event", "received").put("bytes", length))
            emit(Installer.install(context, texts, file, launch, emit))
        } finally {
            file.delete()
        }
    }

    private fun copyExactly(input: InputStream, output: OutputStream, length: Long) {
        val buf = ByteArray(256 * 1024)
        var left = length
        while (left > 0) {
            val n = input.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
            if (n < 0) throw EOFException("Transfer interrupted")
            output.write(buf, 0, n)
            left -= n
        }
    }

    private fun readRequest(input: InputStream): Request? {
        val line = readLine(input) ?: return null
        val parts = line.split(' ')
        if (parts.size < 2) return null
        val headers = HashMap<String, String>()
        while (true) {
            val h = readLine(input) ?: return null
            if (h.isEmpty()) break
            val colon = h.indexOf(':')
            if (colon > 0) headers[h.substring(0, colon).trim().lowercase()] = h.substring(colon + 1).trim()
        }
        val target = parts[1]
        val q = target.indexOf('?')
        val path = if (q >= 0) target.substring(0, q) else target
        val query = if (q >= 0) {
            target.substring(q + 1).split('&').filter { it.isNotEmpty() }.associate {
                val eq = it.indexOf('=')
                if (eq < 0) decode(it) to "" else decode(it.substring(0, eq)) to decode(it.substring(eq + 1))
            }
        } else {
            emptyMap()
        }
        return Request(parts[0].uppercase(), path, query, headers)
    }

    private fun decode(s: String) = URLDecoder.decode(s, "UTF-8")

    /** Читает строку до CRLF; ограничение длины защищает от мусора в сокете. */
    private fun readLine(input: InputStream): String? {
        val sb = StringBuilder()
        while (true) {
            val c = input.read()
            if (c < 0) return if (sb.isEmpty()) null else sb.toString()
            if (c == '\n'.code) return sb.toString().trimEnd('\r')
            sb.append(c.toChar())
            if (sb.length > 8192) throw IOException("Header line too long")
        }
    }

    private fun respond(out: OutputStream, code: Int, json: JSONObject) {
        val body = json.toString().toByteArray(Charsets.UTF_8)
        val reason = when (code) {
            200 -> "OK"; 400 -> "Bad Request"; 401 -> "Unauthorized"; 404 -> "Not Found"
            411 -> "Length Required"; 429 -> "Too Many Requests"; 507 -> "Insufficient Storage"
            else -> "Error"
        }
        out.write(
            ("HTTP/1.1 $code $reason\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: ${body.size}\r\n" +
                "Connection: close\r\n\r\n").toByteArray()
        )
        out.write(body)
    }
}
