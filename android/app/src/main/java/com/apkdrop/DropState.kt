package com.apkdrop

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class LogEntry(val time: Long, val text: String, val ok: Boolean?)

/** Общее состояние процесса, которое показывает UI. */
object DropState {
    private val _running = MutableStateFlow(false)
    val running: StateFlow<Boolean> = _running.asStateFlow()

    private val _log = MutableStateFlow<List<LogEntry>>(emptyList())
    val log: StateFlow<List<LogEntry>> = _log.asStateFlow()

    /** Видна ли сейчас наша activity — тогда можно сразу показать системный диалог установки. */
    @Volatile
    var activityVisible = false

    fun setRunning(value: Boolean) {
        _running.value = value
    }

    /** ok: true — успех, false — ошибка, null — информационное сообщение. */
    fun log(text: String, ok: Boolean? = null) {
        _log.update { (listOf(LogEntry(System.currentTimeMillis(), text, ok)) + it).take(100) }
    }
}
