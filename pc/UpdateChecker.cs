using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApkDrop;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Sha256);

/// <param name="Asset">Файл этого релиза для текущего варианта программы (автономный или обычный); null — такого файла в релизе нет.</param>
public sealed record UpdateInfo(string Tag, Version Version, string Notes, string PageUrl, UpdateAsset? Asset);

/// <summary>
/// Поиск и установка обновлений через GitHub Releases. Релиз должен содержать
/// ApkDrop.exe (обычный) и ApkDrop-standalone.exe (с .NET внутри); тег — «1.8» или «v1.8».
/// </summary>
static class UpdateChecker
{
    const string Repo = "bapuk01/ApkDrop";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";

#if STANDALONE
    /// <summary>Автономная сборка (.NET внутри exe): обновляется файлом ApkDrop-standalone.exe.</summary>
    public const bool Standalone = true;
#else
    public const bool Standalone = false;
#endif

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        // Таймауты задаём на каждый запрос: скачивание 70 МБ по медленному каналу может идти долго.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ApkDrop-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version Current => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>«v1.8», «1.8.0», «1.8-beta» → 1.8. Всё после первой нецифровой/неточечной части отбрасывается.</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        var match = Regex.Match(tag ?? "", @"\d+(\.\d+){0,3}");
        if (!match.Success) return false;
        var text = match.Value;
        if (!text.Contains('.')) text += ".0"; // Version требует минимум «major.minor»
        return Version.TryParse(text, out version!);
    }

    /// <summary>Нужный файл релиза: автономная сборка берёт *standalone*.exe, обычная — любой другой .exe.</summary>
    public static UpdateAsset? PickAsset(IEnumerable<UpdateAsset> assets, bool standalone) =>
        assets
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(a => a.Name.Contains("standalone", StringComparison.OrdinalIgnoreCase) == standalone);

    /// <summary>Последний опубликованный релиз (черновики и пре-релизы GitHub сюда не отдаёт); null — релизов нет.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                assets.Add(new UpdateAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
            }
        }

        return new UpdateInfo(
            tag, version,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var url) ? url.GetString() ?? ReleasesUrl : ReleasesUrl,
            PickAsset(assets, Standalone));
    }

    /// <summary>
    /// Скачивает файл в <paramref name="target"/> (рядом с exe — чтобы потом подменить его переименованием)
    /// и сверяет размер и SHA-256 с данными GitHub. Без контрольной суммы в релизе файл не принимается:
    /// подмена по дороге или на чужом зеркале не пройдёт. При любой ошибке частичный файл удаляется.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, string target, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (asset.Sha256 == null) throw new InvalidDataException(L.T("в релизе нет контрольной суммы файла", "the release has no checksum for this file"));

        try
        {
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    progress.Report((done, total));
                }
            }

            if (asset.Size > 0 && done != asset.Size)
                throw new InvalidDataException(L.T("файл скачался не полностью", "the file was not downloaded completely"));
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("скачанный файл не прошёл проверку контрольной суммы", "the downloaded file failed the checksum check"));
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Подменяет запущенный exe новым и запускает его. Работающий exe нельзя перезаписать, но можно переименовать,
    /// поэтому старый уходит в «.old» (удаляется при следующем запуске).
    /// </summary>
    public static void ReplaceSelfAndStart(string newFile)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(old, exe); // не вышло — возвращаем прежний файл
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Убирает остатки прошлого обновления.</summary>
    public static void CleanupOld()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".new" })
        {
            try { File.Delete(leftover); } catch { /* ещё занят предыдущим процессом — уберём в следующий раз */ }
        }
    }

    /// <summary>
    /// Описание релиза без разметки Markdown — для окна обновления. Если в описании есть линия «---»,
    /// то до неё — русский текст, после — краткий английский: каждому показываем свой.
    /// </summary>
    public static string PlainNotes(string markdown, bool english)
    {
        var parts = Regex.Split(markdown.Replace("\r", ""), @"^[ \t]*-{3,}[ \t]*$", RegexOptions.Multiline);
        var text = english && parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1] : parts[0];

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            line = Regex.Replace(line, @"^#{1,6}\s*", "");
            line = Regex.Replace(line, @"^[-*]\s+", "• ");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = line.Replace("**", "").Replace("`", "");
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
