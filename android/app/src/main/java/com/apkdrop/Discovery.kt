package com.apkdrop

import android.content.Context
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import kotlin.concurrent.thread

/** Отвечает на UDP-броадкаст "APKDROP?" от ПК, чтобы телефон находился без ввода IP. */
class Discovery(private val context: Context) {
    private var socket: DatagramSocket? = null

    fun start() {
        val s = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(DISCOVERY_PORT))
        }
        socket = s
        thread(name = "apkdrop-discovery", isDaemon = true) {
            val buf = ByteArray(512)
            while (!s.isClosed) {
                val packet = DatagramPacket(buf, buf.size)
                try {
                    s.receive(packet)
                } catch (e: IOException) {
                    break
                }
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                if (!text.startsWith("APKDROP?")) continue
                val reply = Net.info(context).toString().toByteArray(Charsets.UTF_8)
                runCatching { s.send(DatagramPacket(reply, reply.size, packet.address, packet.port)) }
            }
        }
    }

    fun stop() {
        runCatching { socket?.close() }
    }
}
