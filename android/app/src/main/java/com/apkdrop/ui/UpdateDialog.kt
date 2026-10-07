package com.apkdrop.ui

import android.content.Intent
import android.net.Uri
import android.text.format.Formatter
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.apkdrop.R
import com.apkdrop.UpdateChecker
import com.apkdrop.UpdateState
import com.apkdrop.UpdateUi

/** Окно «Доступно обновление» → скачивание → (при ошибке) сообщение. Само появляется поверх экрана. */
@Composable
internal fun UpdateDialogHost() {
    val context = LocalContext.current
    val state by UpdateState.ui.collectAsState()

    when (val s = state) {
        UpdateUi.None -> Unit

        is UpdateUi.Available -> AlertDialog(
            onDismissRequest = { UpdateState.later() },
            title = { Text(stringResource(R.string.upd_title)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(stringResource(R.string.upd_text, s.release.versionText, s.current))
                    // Язык описания — как у интерфейса приложения: русский или (для всех остальных) английский.
                    val russian = context.resources.configuration.locales[0].language == "ru"
                    val notes = UpdateChecker.plainNotes(s.release.notes, english = !russian)
                    if (notes.isNotEmpty()) {
                        // Прокручивается и фокусируется — чтобы описание можно было читать пультом.
                        Box(Modifier.heightIn(max = 200.dp).verticalScroll(rememberScrollState()).focusable()) {
                            Text(notes, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                    TextButton(onClick = { UpdateState.skip(context, s.release) }, modifier = Modifier.focusRing()) {
                        Text(stringResource(R.string.upd_skip))
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { UpdateState.startUpdate(context, s.release) }, modifier = Modifier.focusRing()) {
                    Text(stringResource(R.string.upd_now))
                }
            },
            dismissButton = {
                TextButton(onClick = { UpdateState.later() }, modifier = Modifier.focusRing()) { Text(stringResource(R.string.upd_later)) }
            },
        )

        is UpdateUi.Downloading -> AlertDialog(
            onDismissRequest = { },
            title = { Text(stringResource(R.string.upd_downloading)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    val fraction = if (s.total > 0) (s.done.toFloat() / s.total).coerceIn(0f, 1f) else 0f
                    LinearProgressIndicator(progress = { fraction }, modifier = Modifier.fillMaxWidth())
                    Text(
                        stringResource(
                            R.string.upd_progress,
                            Formatter.formatShortFileSize(context, s.done),
                            Formatter.formatShortFileSize(context, s.total),
                        ),
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = { UpdateState.later() }, modifier = Modifier.focusRing()) { Text(stringResource(R.string.upd_cancel)) }
            },
        )

        is UpdateUi.Failed -> AlertDialog(
            onDismissRequest = { UpdateState.dismissFailure() },
            title = { Text(stringResource(R.string.upd_failed_title)) },
            text = { Text(s.message) },
            confirmButton = {
                TextButton(
                    onClick = {
                        runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(s.release.pageUrl))) }
                        UpdateState.dismissFailure()
                    },
                    modifier = Modifier.focusRing(),
                ) { Text(stringResource(R.string.upd_open_page)) }
            },
            dismissButton = {
                TextButton(onClick = { UpdateState.dismissFailure() }, modifier = Modifier.focusRing()) { Text(stringResource(R.string.upd_close)) }
            },
        )
    }
}
