package com.apkdrop

import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket

/**
 * Клиентская часть протокола ApkDrop: отправка APK на другие устройства с ApkDrop.
 * Тот же протокол, что у ПК-клиента (GET /info, GET /auth, POST /install, UDP-поиск).
 *
 * Работает на обычном сокете, а не на HttpURLConnection: так не нужно разрешать
 * незашифрованный HTTP для всего приложения — он нужен только внутри локальной сети.
 */
object SenderClient {
    /** Итог отправки. [httpCode] != 0 — устройство отклонило запрос до установки. */
    class Outcome(
        val ok: Boolean,
        val message: String = "",
        val launched: Boolean = false,
        val launchError: String? = null,
        val httpCode: Int = 0,
        val selfUpdate: Boolean = false,
        val noResult: Boolean = false,
    )

    private class Response(val code: Int, val headers: Map<String, String>, val input: InputStream)

    private const val CONNECT_MS = 4_000
    private const val QUICK_READ_MS = 8_000
    private const val WRITE_PHASE_READ_MS = 60_000
    private const val INSTALL_READ_MS = 15 * 60_000

    // ---------- Поиск ----------

    /** UDP-броадкаст "APKDROP?" во все локальные сети; устройства отвечают тем же JSON, что и /info. */
    fun discover(ownId: String, waitMs: Int = 1500): List<Pair<JSONObject, String>> {
        val found = LinkedHashMap<String, Pair<JSONObject, String>>()
        val socket = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(0))
        }
        socket.use {
            val message = "APKDROP?".toByteArray()
            val targets = broadcastAddresses()
            val deadline = System.currentTimeMillis() + waitMs
            var nextSend = 0L
            var sent = 0
            val buf = ByteArray(2048)
            while (true) {
                val now = System.currentTimeMillis()
                if (now >= deadline) break
                if (sent < 3 && now >= nextSend) {
                    for (t in targets) runCatching { socket.send(DatagramPacket(message, message.size, t, DISCOVERY_PORT)) }
                    sent++
                    nextSend = now + 300
                }
                socket.soTimeout = maxOf(50L, minOf(200L, deadline - now)).toInt()
                val packet = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(packet)
                } catch (e: java.net.SocketTimeoutException) {
                    continue
                }
                val json = runCatching { JSONObject(String(packet.data, 0, packet.length, Charsets.UTF_8)) }.getOrNull() ?: continue
                if (json.optString("app") != "apkdrop") continue
                val id = json.optString("id")
                if (id.isEmpty() || id == ownId) continue
                found[id] = json to (packet.address.hostAddress ?: continue)
            }
        }
        return found.values.toList()
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val result = LinkedHashSet<InetAddress>()
        result.add(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces().toList()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) {
                    val b = ia.broadcast
                    if (b is Inet4Address) result.add(b)
                }
            }
        }
        return result.toList()
    }

    // ---------- Запросы ----------

    /** Информация об устройстве; null, если по адресу отвечает не ApkDrop. */
    fun info(host: String, port: Int, lang: String): JSONObject? {
        val (code, body) = get(host, port, "/info", null, lang)
        if (code != 200) return null
        val json = runCatching { JSONObject(body) }.getOrNull() ?: return null
        return json.takeIf { it.optString("app") == "apkdrop" }
    }

    /** HTTP-код проверки PIN: 200 — верный, 401 — неверный, 429 — заблокирован. */
    fun auth(host: String, port: Int, pin: String?, lang: String): Int = get(host, port, "/auth", pin, lang).first

    private fun get(host: String, port: Int, path: String, pin: String?, lang: String): Pair<Int, String> {
        connect(host, port, QUICK_READ_MS).use { socket ->
            socket.getOutputStream().apply {
                write(requestHead("GET", path, host, pin, lang).toByteArray())
                flush()
            }
            val response = readResponse(BufferedInputStream(socket.getInputStream()))
            return response.code to readBody(response)
        }
    }

    /**
     * Отправляет файл и читает поток событий установки (received, parsed, installing, confirm, done).
     * Промежуточные события уходят в [onEvent]; прогресс передачи — в [onProgress] (0..1).
     */
    fun install(
        host: String, port: Int, pin: String, file: File, launch: Boolean, lang: String,
        onProgress: (Float) -> Unit, onEvent: (JSONObject) -> Unit,
    ): Outcome {
        connect(host, port, WRITE_PHASE_READ_MS).use { socket ->
            val length = file.length()
            val out = BufferedOutputStream(socket.getOutputStream(), 64 * 1024)
            val head = requestHead(
                "POST", "/install" + if (launch) "?launch=1" else "", host, pin, lang,
                "Content-Type: application/vnd.android.package-archive\r\nContent-Length: $length\r\n",
            )
            out.write(head.toByteArray())
            file.inputStream().use { input ->
                val buf = ByteArray(256 * 1024)
                var sent = 0L
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n)
                    sent += n
                    onProgress(sent.toFloat() / length)
                }
            }
            out.flush()

            // Дальше телефон ждёт нажатия «Установить» — это может занять минуты.
            socket.soTimeout = INSTALL_READ_MS
            val input = BufferedInputStream(socket.getInputStream())
            var selfUpdate = false
            try {
                val response = readResponse(input)
                if (response.code != 200) {
                    val message = runCatching { JSONObject(readBody(response)).optString("message") }.getOrDefault("")
                    return Outcome(false, message, httpCode = response.code)
                }
                while (true) {
                    val line = readLine(input) ?: break
                    if (line.isEmpty()) continue
                    val event = runCatching { JSONObject(line) }.getOrNull() ?: continue
                    when (event.optString("event")) {
                        "self-update" -> {
                            selfUpdate = true
                            onEvent(event)
                        }
                        "done" -> return Outcome(
                            ok = event.optBoolean("ok"),
                            message = event.optString("message"),
                            launched = event.optBoolean("launched"),
                            launchError = event.optString("launchError").takeIf { it.isNotEmpty() },
                        )
                        else -> onEvent(event)
                    }
                }
            } catch (e: IOException) {
                // При обновлении самого ApkDrop процесс на той стороне убивается — обрыв связи ожидаем.
                if (!selfUpdate) throw e
            }
            return if (selfUpdate) Outcome(true, selfUpdate = true) else Outcome(false, noResult = true)
        }
    }

    // ---------- Низкий уровень ----------

    private fun connect(host: String, port: Int, readTimeoutMs: Int): Socket {
        val socket = Socket()
        try {
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(host, port), CONNECT_MS)
            socket.soTimeout = readTimeoutMs
        } catch (e: IOException) {
            runCatching { socket.close() }
            throw e
        }
        return socket
    }

    private fun requestHead(method: String, path: String, host: String, pin: String?, lang: String, extra: String = ""): String {
        // Всё, что попадает в заголовки, чистим от пробелов и переводов строк.
        val safeHost = host.filter { !it.isWhitespace() }
        val safePin = pin?.filter { it.isLetterOrDigit() }
        return "$method $path HTTP/1.1\r\n" +
            "Host: $safeHost\r\n" +
            "Accept-Language: ${lang.filter { it.isLetter() || it == '-' }.ifEmpty { "en" }}\r\n" +
            (if (!safePin.isNullOrEmpty()) "X-Pin: $safePin\r\n" else "") +
            extra +
            "Connection: close\r\n\r\n"
    }

    private fun readResponse(input: InputStream): Response {
        val status = readLine(input) ?: throw IOException("Empty response")
        val code = status.split(' ').getOrNull(1)?.toIntOrNull() ?: throw IOException("Bad response: $status")
        val headers = HashMap<String, String>()
        while (true) {
            val line = readLine(input) ?: break
            if (line.isEmpty()) break
            val colon = line.indexOf(':')
            if (colon > 0) headers[line.substring(0, colon).trim().lowercase()] = line.substring(colon + 1).trim()
        }
        return Response(code, headers, input)
    }

    private fun readBody(response: Response): String {
        val length = response.headers["content-length"]?.toIntOrNull()
        val bytes = if (length != null) {
            val data = ByteArray(length)
            var read = 0
            while (read < length) {
                val n = response.input.read(data, read, length - read)
                if (n < 0) break
                read += n
            }
            data.copyOf(read)
        } else {
            response.input.readBytes()
        }
        return String(bytes, Charsets.UTF_8)
    }

    /** Строка до LF; байты собираем и декодируем как UTF-8 целиком — в сообщениях есть кириллица. */
    private fun readLine(input: InputStream): String? {
        val buf = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return if (buf.size() == 0) null else buf.toString(Charsets.UTF_8.name()).trimEnd('\r')
            if (b == '\n'.code) return buf.toString(Charsets.UTF_8.name()).trimEnd('\r')
            buf.write(b)
            if (buf.size() > 64 * 1024) throw IOException("Line too long")
        }
    }
}
