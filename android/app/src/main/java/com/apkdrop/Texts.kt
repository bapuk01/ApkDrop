package com.apkdrop

import android.content.Context
import android.content.res.Configuration
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

/**
 * Строки на языке собеседника. Экран и журнал телефона — на языке телефона,
 * а ответы ПК-клиенту — на языке из его заголовка Accept-Language.
 */
object Texts {
    private val cache = ConcurrentHashMap<String, Context>()

    fun forLanguage(context: Context, acceptLanguage: String?): Context {
        // "ru", "en-US,en;q=0.9" → первый язык из списка.
        val lang = acceptLanguage?.split(',', ';', '-')?.firstOrNull()?.trim()?.lowercase()
        if (lang.isNullOrEmpty()) return context
        return cache.getOrPut(lang) {
            val config = Configuration(context.resources.configuration)
            config.setLocale(Locale.forLanguageTag(lang))
            context.createConfigurationContext(config)
        }
    }
}
