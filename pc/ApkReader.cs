using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ApkDrop;

public sealed record ApkInfo(string Package, long VersionCode, string VersionName, string Label, string Path, DateTime Modified)
{
    public string Version => VersionName == "" ? $"({VersionCode})" : $"{VersionName} ({VersionCode})";

    /// <summary>SHA-256 всего файла — отличает сборки с одинаковым versionCode.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>SHA-256 сертификата подписи (как у apksigner); null — подпись v2/v3 не найдена.</summary>
    public string? CertSha256 { get; init; }
}

/// <summary>
/// Читает package, versionCode, versionName и название приложения прямо из APK:
/// бинарный AndroidManifest.xml (AXML) + resources.arsc для ссылок вида @string/app_name.
/// </summary>
public static class ApkReader
{
    const uint AttrLabel = 0x01010001;
    const uint AttrVersionCode = 0x0101021b;
    const uint AttrVersionName = 0x0101021c;
    const uint AttrVersionCodeMajor = 0x01010576;

    const byte TypeReference = 0x01;
    const byte TypeString = 0x03;
    const byte TypeIntDec = 0x10;
    const byte TypeIntHex = 0x11;

    readonly record struct Value(string? Raw, byte Type, uint Data);

    public static ApkInfo Read(string path)
    {
        byte[] manifest;
        byte[]? arsc;
        using (var zip = ZipFile.OpenRead(path))
        {
            manifest = ReadEntry(zip, "AndroidManifest.xml") ?? throw new InvalidDataException(L.T("в файле нет AndroidManifest.xml", "the file has no AndroidManifest.xml"));
            arsc = ReadEntry(zip, "resources.arsc");
        }

        string pkg = "";
        long code = 0, major = 0;
        Value? versionName = null, label = null;
        string[] pool = Array.Empty<string>();
        uint[] resMap = Array.Empty<uint>();

        int pos = U16(manifest, 2); // пропускаем заголовок XML-документа
        while (pos + 8 <= manifest.Length)
        {
            var type = U16(manifest, pos);
            var headerSize = U16(manifest, pos + 2);
            var size = (int)U32(manifest, pos + 4);
            if (size < 8 || pos + size > manifest.Length) break;

            switch (type)
            {
                case 0x0001:
                    pool = ReadStringPool(manifest, pos);
                    break;

                case 0x0180:
                    resMap = new uint[(size - headerSize) / 4];
                    for (var i = 0; i < resMap.Length; i++) resMap[i] = U32(manifest, pos + headerSize + i * 4);
                    break;

                case 0x0102: // начало элемента
                    var ext = pos + headerSize;
                    var element = Str(pool, U32(manifest, ext + 4));
                    if (element is not ("manifest" or "application")) break;
                    int attrStart = U16(manifest, ext + 8), attrSize = U16(manifest, ext + 10), attrCount = U16(manifest, ext + 12);
                    for (var i = 0; i < attrCount; i++)
                    {
                        var a = ext + attrStart + i * attrSize;
                        var nameIndex = U32(manifest, a + 4);
                        var raw = U32(manifest, a + 8);
                        var value = new Value(raw == uint.MaxValue ? null : Str(pool, raw), manifest[a + 15], U32(manifest, a + 16));
                        var name = Str(pool, nameIndex);
                        var resId = nameIndex < resMap.Length ? resMap[nameIndex] : 0;

                        if (element == "manifest")
                        {
                            if (name == "package" && resId == 0) pkg = value.Raw ?? Str(pool, value.Data);
                            else if (resId == AttrVersionCode || name == "versionCode") code = AsInt(value);
                            else if (resId == AttrVersionCodeMajor || name == "versionCodeMajor") major = AsInt(value);
                            else if (resId == AttrVersionName || name == "versionName") versionName = value;
                        }
                        else if (resId == AttrLabel || name == "label")
                        {
                            label = value;
                        }
                    }
                    break;
            }
            pos += size;
        }

        if (pkg == "") throw new InvalidDataException(L.T("не удалось прочитать имя пакета", "could not read the package name"));
        string Resolve(Value? v) => v is { } x ? ResolveValue(x, pool, arsc) ?? "" : "";
        var labelText = Resolve(label);
        using var file = File.OpenRead(path);
        var cert = SignerCertSha256(file);
        file.Position = 0;
        return new ApkInfo(pkg, (major << 32) | (uint)code, Resolve(versionName),
            labelText == "" ? pkg : labelText, path, File.GetLastWriteTime(path))
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),
            CertSha256 = cert,
        };
    }

    /// <summary>
    /// Сертификат первого подписанта из APK Signing Block (схемы v3.1/v3/v2).
    /// Формат файла: …данные ZIP | блок подписи | central directory | EOCD.
    /// </summary>
    static string? SignerCertSha256(FileStream file)
    {
        try
        {
            // EOCD — в последних 64 КБ + 22 байта; смещение central directory лежит по +16.
            var tailLength = (int)Math.Min(file.Length, 65_557);
            var tail = new byte[tailLength];
            file.Position = file.Length - tailLength;
            file.ReadExactly(tail);
            var eocd = -1;
            for (var i = tailLength - 22; i >= 0; i--)
            {
                if (U32(tail, i) == 0x06054b50) { eocd = i; break; }
            }
            if (eocd < 0) return null;
            long cdOffset = U32(tail, eocd + 16);
            if (cdOffset < 32) return null;

            // Конец блока: size (u64) + магия "APK Sig Block 42".
            var footer = new byte[24];
            file.Position = cdOffset - 24;
            file.ReadExactly(footer);
            if (Encoding.ASCII.GetString(footer, 8, 16) != "APK Sig Block 42") return null;
            var blockSize = (long)BitConverter.ToUInt64(footer, 0);
            if (blockSize < 24 || blockSize > 64 * 1024 * 1024) return null;

            // Пары ID → значение лежат между начальным полем size и концевым size+магией.
            var pairs = new byte[blockSize - 24];
            file.Position = cdOffset - blockSize;
            file.ReadExactly(pairs);

            var found = new Dictionary<uint, int>();
            var p = 0;
            while (p + 12 <= pairs.Length)
            {
                var len = (long)BitConverter.ToUInt64(pairs, p);
                if (len < 4 || p + 8 + len > pairs.Length) break;
                found[U32(pairs, p + 8)] = p + 12;
                p += 8 + (int)len;
            }

            foreach (var id in new uint[] { 0x1b93ad61, 0xf05368c0, 0x7109871a }) // v3.1, v3, v2
            {
                if (!found.TryGetValue(id, out var v)) continue;
                // signers(len) → signer(len) → signed data(len) → digests(len) → certificates(len) → cert(len)
                var signedData = v + 4 + 4 + 4;
                var digestsLength = (int)U32(pairs, signedData);
                var certificates = signedData + 4 + digestsLength;
                var certLength = (int)U32(pairs, certificates + 4);
                var cert = pairs.AsSpan(certificates + 8, certLength);
                return Convert.ToHexString(SHA256.HashData(cert)).ToLowerInvariant();
            }
        }
        catch
        {
            // Нестандартный APK — просто не знаем подпись.
        }
        return null;
    }

    static long AsInt(Value v) =>
        v.Type is TypeIntDec or TypeIntHex ? v.Data : long.TryParse(v.Raw, out var n) ? n : 0;

    static string? ResolveValue(Value v, string[] pool, byte[]? arsc)
    {
        if (v.Raw != null) return v.Raw;
        return v.Type switch
        {
            TypeString => Str(pool, v.Data),
            TypeReference when arsc != null => ResolveResource(arsc, v.Data, 0),
            TypeIntDec => v.Data.ToString(),
            _ => null,
        };
    }

    /// <summary>Находит строковое значение ресурса; предпочитает конфигурацию по умолчанию (без языка).</summary>
    static string? ResolveResource(byte[] arsc, uint resId, int depth)
    {
        if (depth > 3) return null;
        int pkgId = (int)(resId >> 24), typeId = (int)((resId >> 16) & 0xFF), entryId = (int)(resId & 0xFFFF);
        string[] globalPool = Array.Empty<string>();
        string? fallback = null;

        var pos = (int)U16(arsc, 2);
        while (pos + 8 <= arsc.Length)
        {
            var type = U16(arsc, pos);
            var headerSize = U16(arsc, pos + 2);
            var size = (int)U32(arsc, pos + 4);
            if (size < 8 || pos + size > arsc.Length) break;

            if (type == 0x0001)
            {
                globalPool = ReadStringPool(arsc, pos);
            }
            else if (type == 0x0200 && U32(arsc, pos + 8) == pkgId)
            {
                var p = pos + headerSize;
                while (p + 8 <= pos + size)
                {
                    var ctype = U16(arsc, p);
                    var chs = U16(arsc, p + 2);
                    var csize = (int)U32(arsc, p + 4);
                    if (csize < 8) break;
                    if (ctype == 0x0201 && arsc[p + 8] == typeId)
                    {
                        var value = ReadTypeEntry(arsc, p, chs, entryId, out var defaultConfig);
                        if (value is { } v)
                        {
                            var text = v.Type switch
                            {
                                TypeString => Str(globalPool, v.Data),
                                TypeReference => ResolveResource(arsc, v.Data, depth + 1),
                                _ => null,
                            };
                            if (text != null)
                            {
                                if (defaultConfig) return text;
                                fallback ??= text;
                            }
                        }
                    }
                    p += csize;
                }
            }
            pos += size;
        }
        return fallback;
    }

    static Value? ReadTypeEntry(byte[] b, int chunk, int headerSize, int entryId, out bool defaultConfig)
    {
        var flags = b[chunk + 9];
        var entryCount = (int)U32(b, chunk + 12);
        var entriesStart = (int)U32(b, chunk + 16);
        var config = chunk + 20;
        defaultConfig = U16(b, config + 8) == 0; // language не задан

        const byte Sparse = 0x01, Offset16 = 0x02;
        var offsets = chunk + headerSize;
        long offset = -1;
        if ((flags & Sparse) != 0)
        {
            for (var i = 0; i < entryCount; i++)
            {
                if (U16(b, offsets + i * 4) == entryId)
                {
                    offset = U16(b, offsets + i * 4 + 2) * 4L;
                    break;
                }
            }
        }
        else if (entryId < entryCount)
        {
            if ((flags & Offset16) != 0)
            {
                var o = U16(b, offsets + entryId * 2);
                if (o != 0xFFFF) offset = o * 4L;
            }
            else
            {
                var o = U32(b, offsets + entryId * 4);
                if (o != uint.MaxValue) offset = o;
            }
        }
        if (offset < 0) return null;

        var e = chunk + entriesStart + (int)offset;
        var size = U16(b, e);
        var entryFlags = U16(b, e + 2);
        const int Complex = 0x1, Compact = 0x8;
        if ((entryFlags & Compact) != 0) return new Value(null, (byte)(entryFlags >> 8), U32(b, e + 4));
        if ((entryFlags & Complex) != 0) return null;
        return new Value(null, b[e + size + 3], U32(b, e + size + 4));
    }

    static string[] ReadStringPool(byte[] b, int pos)
    {
        var headerSize = U16(b, pos + 2);
        var count = (int)U32(b, pos + 8);
        var utf8 = (U32(b, pos + 16) & 0x100) != 0;
        var stringsStart = (int)U32(b, pos + 20);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            try
            {
                var p = pos + stringsStart + (int)U32(b, pos + headerSize + i * 4);
                if (utf8)
                {
                    p += (b[p] & 0x80) != 0 ? 2 : 1; // длина в символах — не нужна
                    int len = b[p];
                    if ((len & 0x80) != 0) { len = ((len & 0x7F) << 8) | b[p + 1]; p += 2; }
                    else p += 1;
                    result[i] = Encoding.UTF8.GetString(b, p, len);
                }
                else
                {
                    int len = U16(b, p);
                    p += 2;
                    if ((len & 0x8000) != 0) { len = ((len & 0x7FFF) << 16) | U16(b, p); p += 2; }
                    result[i] = Encoding.Unicode.GetString(b, p, len * 2);
                }
            }
            catch
            {
                result[i] = "";
            }
        }
        return result;
    }

    static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string Str(string[] pool, uint index) => index < pool.Length ? pool[index] : "";
    static ushort U16(byte[] b, int p) => BitConverter.ToUInt16(b, p);
    static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);
}
