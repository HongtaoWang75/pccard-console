namespace PcCard.Core.Cloud;

/// <summary>
/// 米家云协议专用的 RC4：加密前丢弃前 1024 字节密钥流（协议约定，两端一致）。
/// 只用于兼容小米云的 ENCRYPT-RC4 算法，与任何自研加密无关。
/// </summary>
public static class Rc4
{
    public static byte[] Crypt(byte[] key, ReadOnlySpan<byte> data, int drop = 1024)
    {
        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;

        int j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        int a = 0, b = 0;
        for (var i = 0; i < drop; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
        }

        var outBuf = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            outBuf[i] = (byte)(data[i] ^ s[(s[a] + s[b]) & 0xFF]);
        }
        return outBuf;
    }

    /// <summary>加密：明文 → base64(密文)。</summary>
    public static string EncryptToBase64(byte[] key, string plaintext)
        => Convert.ToBase64String(Crypt(key, System.Text.Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>解密：base64(密文) → 明文字节。</summary>
    public static byte[] DecryptFromBase64(byte[] key, string cipherBase64)
        => Crypt(key, Convert.FromBase64String(cipherBase64));
}
