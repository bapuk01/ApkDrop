using System.Globalization;

namespace ApkDrop;

/// <summary>
/// Два языка интерфейса. Каждая строка пишется в коде парой: T("по-русски", "in English") —
/// так перевод всегда рядом с исходной фразой и ничего не теряется при правках.
/// </summary>
static class L
{
    /// <summary>true — английский интерфейс.</summary>
    public static bool En { get; private set; }

    /// <summary>Код языка для заголовка Accept-Language: телефон ответит на нём же.</summary>
    public static string Code => En ? "en" : "ru";

    /// <param name="setting">"ru", "en" или "auto" — по языку Windows.</param>
    public static void Init(string? setting)
    {
        En = setting switch
        {
            "en" => true,
            "ru" => false,
            // Для русско-говорящих систем по умолчанию русский, для остальных — английский.
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is not ("ru" or "uk" or "be" or "kk"),
        };
    }

    public static string T(string ru, string en) => En ? en : ru;

    // Статусы телефона, по которым раскрашивается список.
    public static string Online => T("в сети", "online");
    public static string NotFound => T("не найден", "not found");
    public static string Offline => T("не в сети", "offline");

    /// <summary>Размер в мегабайтах с подписью на нужном языке.</summary>
    public static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} {T("МБ", "MB")}";
}
