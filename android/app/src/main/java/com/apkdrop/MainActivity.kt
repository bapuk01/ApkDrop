package com.apkdrop

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.mutableIntStateOf
import androidx.core.content.ContextCompat
import com.apkdrop.ui.ApkDropApp
import com.apkdrop.ui.Actions

class MainActivity : ComponentActivity() {

    /** Увеличивается при возврате на экран — чтобы перечитать статусы разрешений. */
    private val resumeTick = mutableIntStateOf(0)

    private val notificationPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { resumeTick.intValue++ }

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(AppLocale.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val prefs = Prefs(this)
        if (prefs.serverEnabled && !DropState.running.value) DropService.start(this)
        if (savedInstanceState == null && !notificationsGranted()) requestNotifications()

        val actions = Actions(
            setServer = { on ->
                prefs.serverEnabled = on
                if (on) DropService.start(this) else DropService.stop(this)
            },
            openInstallSettings = {
                open(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            },
            requestNotifications = ::requestNotifications,
            requestBattery = ::requestBatteryExemption,
            currentLanguage = { AppLocale.current(this) },
            setLanguage = { AppLocale.set(this, it) },
        )
        setContent {
            ApkDropApp(prefs = prefs, tick = resumeTick.intValue, actions = actions)
        }
    }

    override fun onResume() {
        super.onResume()
        DropState.activityVisible = true
        resumeTick.intValue++
    }

    override fun onPause() {
        DropState.activityVisible = false
        super.onPause()
    }

    private fun notificationsGranted() =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED

    private fun requestNotifications() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    @SuppressLint("BatteryLife")
    private fun requestBatteryExemption() {
        open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
    }

    private fun open(intent: Intent) {
        runCatching { startActivity(intent) }
            .onFailure { runCatching { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) } }
    }
}
