using System.Security.Cryptography;
using System.Text;

namespace PcCard.Core.Cloud;

/// <summary>
/// 小米云 ENCRYPT-RC4 签名（与 mijiaAPI/micloud 的 miutils 逐行对齐）：
///
///   nonce        = b64(8 随机字节 + 分钟时间戳大端最少字节)
///   signed_nonce = b64(sha256(b64d(ssecurity) + b64d(nonce)))
///   rc4_hash__   = b64(sha1(METHOD &amp; uri &amp; k=v… &amp; signed_nonce))   ← 明文参数上算
///   随后全部参数值 RC4(signed_nonce) 后 b64
///   signature    = 同 sha1 公式（对加密后的参数再算一次）
///   最终表单顺序 = data, rc4_hash__, signature, ssecurity, _nonce
///
/// 签名对参数**顺序**敏感 —— 这里用有序 List 而不是 Dictionary。
/// </summary>
public static class MiCloudSigner
{
    public static string GenNonce()
    {
        Span<byte> buf = stackalloc byte[12];
        RandomNumberGenerator.Fill(buf[..8]);

        var part2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000;
        var len = 1;
        for (var t = part2; t > 0; t >>= 8) len = Math.Max(len, (int)((64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)t) + 7) / 8));
        for (var i = 0; i < len; i++)
            buf[8 + i] = (byte)(part2 >> (8 * (len - 1 - i)));

        return Convert.ToBase64String(buf[..(8 + len)]);
    }

    public static string SignedNonce(string ssecurityBase64, string nonceBase64)
    {
        var raw = new byte[64];
        var a = Convert.FromBase64String(ssecurityBase64);
        var b = Convert.FromBase64String(nonceBase64);
        a.CopyTo(raw, 0);
        b.CopyTo(raw, a.Length);
        return Convert.ToBase64String(SHA256.HashData(raw.AsSpan(0, a.Length + b.Length)));
    }

    public static string Sign(string uri, string method, string signedNonce, IReadOnlyList<KeyValuePair<string, string>> orderedParams)
    {
        var sb = new StringBuilder();
        sb.Append(method.ToUpperInvariant()).Append('&').Append(uri);
        foreach (var kv in orderedParams)
            sb.Append('&').Append(kv.Key).Append('=').Append(kv.Value);
        sb.Append('&').Append(signedNonce);
        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>构造最终表单（保持签名用顺序）。</summary>
    public static List<KeyValuePair<string, string>> BuildEncryptedForm(
        string uri, string dataJson, string nonce, string signedNonce, string ssecurity)
    {
        // 1) 明文参数（只含 data）上算 rc4_hash__
        var plain = new List<KeyValuePair<string, string>> { new("data", dataJson) };
        var rc4Hash = Sign(uri, "POST", signedNonce, plain);

        // 2) 全部值 RC4 加密（含 rc4_hash__ 本身）
        var key = Convert.FromBase64String(signedNonce);
        var enc = new List<KeyValuePair<string, string>>
        {
            new("data", Rc4.EncryptToBase64(key, dataJson)),
            new("rc4_hash__", Rc4.EncryptToBase64(key, rc4Hash)),
        };

        // 3) 对加密后的参数算 signature，随后追加 ssecurity / _nonce
        var signature = Sign(uri, "POST", signedNonce, enc);
        enc.Add(new("signature", signature));
        enc.Add(new("ssecurity", ssecurity));
        enc.Add(new("_nonce", nonce));
        return enc;
    }

    /// <summary>解密响应体：base64 密文 → 明文（UTF-8 或内层 gzip）。</summary>
    public static string DecryptResponse(string nonce, string ssecurityBase64, string bodyBase64)
    {
        var sn = SignedNonce(ssecurityBase64, nonce);
        var key = Convert.FromBase64String(sn);
        var raw = Rc4.DecryptFromBase64(key, bodyBase64);

        // gzip 头（0x1F 0x8B）直接识别
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
            return GzipDecompress(raw);

        try
        {
            // ★ 必须用严格解码器：默认的 Encoding.UTF8.GetString 遇到非法字节会
            //   替换成 U+FFFD 而不抛异常，gzip 回退分支就永远走不到了。
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return GzipDecompress(raw);
        }
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string GzipDecompress(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
