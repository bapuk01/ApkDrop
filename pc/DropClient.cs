using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ApkDrop;

/// <param name="Protocol">Версия протокола ApkDrop на телефоне: 1 — только установка, 2 — /packages, 3 — хеш и подпись установленного APK.</param>
public sealed record PhoneInfo(string Id, string Name, string Model, string Android, int Port, bool SilentUpdates, bool CanInstall, int Protocol)
{
    public static PhoneInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (Str(r, "app") != "apkdrop") return null;
            return new PhoneInfo(
                Str(r, "id"), Str(r, "name"), Str(r, "model"), Str(r, "android"),
                r.TryGetProperty("port", out var p) ? p.GetInt32() : DropClient.DefaultPort,
                r.TryGetProperty("silentUpdates", out var s) && s.GetBoolean(),
                !r.TryGetProperty("canInstall", out var c) || c.GetBoolean(),
                r.TryGetProperty("protocol", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1);
        }
        catch
        {
            return null;
        }
    }

    internal static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

public enum PinCheck { Ok, Wrong, Locked }

/// <param name="Sha256">SHA-256 установленного APK (ApkDrop 1.3+); null — телефон не сообщает или split APK.</param>
/// <param name="Certs">SHA-256 сертификатов подписи установленного приложения (ApkDrop 1.3+).</param>
/// <param name="LastUpdate">Когда приложение последний раз устанавливалось/обновлялось на телефоне (ApkDrop 1.3+).</param>
public sealed record InstalledApp(long VersionCode, string VersionName, string? Installer, string? Sha256,
    IReadOnlyList<string> Certs, DateTime? LastUpdate);

/// <summary>На телефоне старая версия ApkDrop без нужного запроса.</summary>
public sealed class OutdatedPhoneException() : Exception(L.T("на телефоне старая версия ApkDrop — обновите её", "the phone has an old ApkDrop — please update it"));

public sealed record InstallOutcome(bool Ok, string Message, bool Launched = false, string? LaunchError = null);

/// <summary>Протокол общения с приложением ApkDrop на телефоне.</summary>
public static class DropClient
{
    public const int DefaultPort = 8765;
    public const int DiscoveryPort = 8766;

    static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) })
    {
        // Ожидание подтверждения на телефоне может длиться минуты — таймауты задаём на каждый запрос.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Телефон отвечает на языке ПК-клиента (заголовок Accept-Language), чтобы журнал был на одном языке.</summary>
    public static void SetLanguage(string code)
    {
        Http.DefaultRequestHeaders.AcceptLanguage.Clear();
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(code);
    }

    static string Url(string host, int port, string path) => $"http://{host}:{port}{path}";

    static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>UDP-броадкаст "APKDROP?" во все локальные сети; телефоны отвечают JSON-ом с /info.</summary>
    public static async Task<List<(PhoneInfo Info, string Host)>> DiscoverAsync(int waitMs = 1500, CancellationToken ct = default)
    {
        var found = new Dictionary<string, (PhoneInfo, string)>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        try
        {
            // SIO_UDP_CONNRESET = off: иначе ICMP «порт недоступен» роняет ReceiveAsync на Windows.
            udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
        }
        catch
        {
        }

        var message = Encoding.ASCII.GetBytes("APKDROP?");
        var targets = BroadcastAddresses();
        using var cts = Linked(ct, TimeSpan.FromMilliseconds(waitMs));

        var sender = Task.Run(async () =>
        {
            for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
            {
                foreach (var t in targets)
                {
                    try { await udp.SendAsync(message, message.Length, new IPEndPoint(t, DiscoveryPort)); }
                    catch { /* сеть без броадкаста — пропускаем */ }
                }
                try { await Task.Delay(300, cts.Token); } catch { break; }
            }
        });

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var info = PhoneInfo.Parse(Encoding.UTF8.GetString(r.Buffer));
                if (info != null && info.Id != "") found[info.Id] = (info, r.RemoteEndPoint.Address.ToString());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
            }
        }
        try { await sender; } catch { }
        return found.Values.ToList();
    }

    static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var addr = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(addr[i] | ~mask[i]);
                list.Add(new IPAddress(b));
            }
        }
        return list.Distinct().ToList();
    }

    public static async Task<PhoneInfo?> InfoAsync(string host, int port, CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(5));
        var json = await Http.GetStringAsync(Url(host, port, "/info"), cts.Token);
        return PhoneInfo.Parse(json);
    }

    public static async Task<PinCheck> CheckPinAsync(string host, int port, string? pin, CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(host, port, "/auth"));
        if (pin != null) req.Headers.TryAddWithoutValidation("X-Pin", pin);
        using var resp = await Http.SendAsync(req, cts.Token);
        return resp.StatusCode switch
        {
            HttpStatusCode.OK => PinCheck.Ok,
            HttpStatusCode.Unauthorized => PinCheck.Wrong,
            HttpStatusCode.TooManyRequests => PinCheck.Locked,
            _ => throw new HttpRequestException(L.T("Неожиданный ответ телефона: ", "Unexpected phone response: ") + (int)resp.StatusCode),
        };
    }

    /// <summary>Установленные на телефоне версии пакетов; отсутствующие пакеты — null.</summary>
    public static async Task<Dictionary<string, InstalledApp?>> PackagesAsync(
        string host, int port, string pin, IEnumerable<string> packages, CancellationToken ct = default)
    {
        // Первый раз телефон считает хеши установленных APK — на слабых TV это может занять время.
        using var cts = Linked(ct, TimeSpan.FromSeconds(60));
        var names = Uri.EscapeDataString(string.Join(",", packages));
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(host, port, "/packages?names=" + names));
        req.Headers.TryAddWithoutValidation("X-Pin", pin);
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new OutdatedPhoneException();
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var result = new Dictionary<string, InstalledApp?>();
        foreach (var p in doc.RootElement.GetProperty("packages").EnumerateObject())
        {
            result[p.Name] = p.Value.ValueKind == JsonValueKind.Object
                ? new InstalledApp(
                    p.Value.GetProperty("versionCode").GetInt64(),
                    PhoneInfo.Str(p.Value, "versionName"),
                    p.Value.TryGetProperty("installer", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null,
                    p.Value.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                    p.Value.TryGetProperty("certs", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : new List<string>(),
                    p.Value.TryGetProperty("lastUpdateTime", out var t) && t.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).LocalDateTime
                        : null)
                : null;
        }
        return result;
    }

    /// <summary>
    /// Отправляет APK и читает NDJSON-поток событий (received, parsed, installing, confirm, done).
    /// Промежуточные события передаются в <paramref name="onEvent"/>.
    /// </summary>
    public static async Task<InstallOutcome> InstallAsync(
        string host, int port, string pin, string apkPath, bool launch,
        IProgress<double>? progress, Action<JsonElement> onEvent, CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromMinutes(15));
        await using var file = new FileStream(apkPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);

        using var req = new HttpRequestMessage(HttpMethod.Post, Url(host, port, "/install" + (launch ? "?launch=1" : "")));
        req.Headers.TryAddWithoutValidation("X-Pin", pin);
        req.Content = new ProgressContent(file, progress);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.android.package-archive");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            string message;
            try { message = PhoneInfo.Str(JsonDocument.Parse(body).RootElement, "message"); }
            catch { message = body; }
            return new InstallOutcome(false, L.T("телефон отклонил файл", "the phone rejected the file") + $" ({(int)resp.StatusCode}): {message}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var selfUpdate = false;
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonElement e;
                using (var doc = JsonDocument.Parse(line)) e = doc.RootElement.Clone();
                var ev = PhoneInfo.Str(e, "event");
                if (ev == "self-update") selfUpdate = true;
                if (ev == "done")
                {
                    return new InstallOutcome(
                        e.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                        PhoneInfo.Str(e, "message"),
                        e.TryGetProperty("launched", out var l) && l.GetBoolean(),
                        e.TryGetProperty("launchError", out var le) ? le.GetString() : null);
                }
                onEvent(e);
            }
        }
        catch (Exception ex) when (selfUpdate && ex is IOException or HttpRequestException)
        {
        }

        return selfUpdate
            ? new InstallOutcome(true, L.T("ApkDrop на телефоне обновился и перезапускается (обрыв связи — это нормально)",
                "ApkDrop on the phone updated and is restarting (the dropped connection is expected)"))
            : new InstallOutcome(false, L.T("телефон закрыл соединение, не сообщив результат", "the phone closed the connection without a result"));
    }

    sealed class ProgressContent(Stream source, IProgress<double>? progress) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[128 * 1024];
            long total = source.Length, sent = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                sent += n;
                progress?.Report((double)sent / total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = source.Length;
            return true;
        }
    }
}
