package com.apkdrop

import android.app.Activity
import android.app.LocaleManager
import android.content.Context
import android.content.res.Configuration
import android.os.Build
import android.os.LocaleList
import java.util.Locale

/**
 * Язык интерфейса ApkDrop, выбранный внутри приложения.
 * Android 13+: системный «язык приложения» (LocaleManager) — система сама перезапускает экран.
 * Android 8–12: запоминаем сами и подменяем конфигурацию контекста activity и сервиса.
 */
object AppLocale {
    /** "" — как в системе, иначе тег языка ("en", "ru"). */
    val choices = listOf("", "en", "ru")

    private const val PREFS = "apkdrop"
    private const val KEY = "app_language"

    fun current(context: Context): String =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val locales = context.getSystemService(LocaleManager::class.java).applicationLocales
            if (locales.isEmpty) "" else locales[0].language
        } else {
            saved(context)
        }

    fun set(activity: Activity, language: String) {
        activity.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, language).apply()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            activity.getSystemService(LocaleManager::class.java).applicationLocales =
                if (language.isEmpty()) LocaleList.getEmptyLocaleList() else LocaleList.forLanguageTags(language)
        } else {
            activity.recreate()
        }
    }

    /** Для Android 8–12: контекст с выбранным языком (вызывается из attachBaseContext). */
    fun wrap(base: Context): Context {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) return base
        val language = saved(base)
        if (language.isEmpty()) return base
        val config = Configuration(base.resources.configuration)
        config.setLocale(Locale.forLanguageTag(language))
        return base.createConfigurationContext(config)
    }

    private fun saved(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, "").orEmpty()
}
