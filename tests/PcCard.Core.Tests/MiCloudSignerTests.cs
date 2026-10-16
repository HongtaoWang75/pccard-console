using System.Text.Json;
using PcCard.Core.Cloud;
using Xunit;

namespace PcCard.Core.Tests;

/// <summary>
/// 跨语言一致性：C# 的签名实现必须与 Python（已被真机验证）逐字节一致。
/// 向量由 tools/wsa/make_vectors.py 生成（合成密钥 + 固定 nonce）。
/// </summary>
public class MiCloudSignerTests
{
    private static JsonDocument LoadVectors()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));

    [Fact]
    public void SignVectors_MatchPythonImplementation()
    {
        using var doc = LoadVectors();
        var meta = doc.RootElement.GetProperty("_meta");
        var ssecurity = meta.GetProperty("ssecurity").GetString()!;
        var nonce = meta.GetProperty("nonce").GetString()!;

        var failures = new List<string>();
        var count = 0;
        foreach (var v in doc.RootElement.GetProperty("sign_vectors").EnumerateArray())
        {
            count++;
            var uri = v.GetProperty("uri").GetString()!;
            var dataJson = v.GetProperty("data_json").GetString()!;
            var expectedSn = v.GetProperty("signed_nonce").GetString()!;
            var expected = v.GetProperty("expected");

            var sn = MiCloudSigner.SignedNonce(ssecurity, nonce);
            if (sn != expectedSn)
                failures.Add($"[{count}] {uri}: signed_nonce 不符\n  C#  {sn}\n  Py  {expectedSn}");

            var form = MiCloudSigner.BuildEncryptedForm(uri, dataJson, nonce, sn, ssecurity);
            var dict = form.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var field in new[] { "data", "rc4_hash__", "signature" })
            {
                var exp = expected.GetProperty(field).GetString()!;
                if (!dict.TryGetValue(field, out var got))
                    failures.Add($"[{count}] {uri}: 表单缺字段 {field}");
                else if (got != exp)
                    failures.Add($"[{count}] {uri}: {field} 不符\n  C#  {got}\n  Py  {exp}");
            }
        }

        Assert.True(count > 0, "向量文件里没有 sign_vectors");
        Assert.True(failures.Count == 0, "跨语言不一致：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DecryptVectors_MatchPythonImplementation()
    {
        using var doc = LoadVectors();
        var meta = doc.RootElement.GetProperty("_meta");
        var ssecurity = meta.GetProperty("ssecurity").GetString()!;
        var nonce = meta.GetProperty("nonce").GetString()!;

        var failures = new List<string>();
        var count = 0;
        foreach (var v in doc.RootElement.GetProperty("decrypt_vectors").EnumerateArray())
        {
            count++;
            var kind = v.GetProperty("kind").GetString()!;
            var body = v.GetProperty("body_b64").GetString()!;
            var expected = v.GetProperty("expected_json").GetString()!;
            var got = MiCloudSigner.DecryptResponse(nonce, ssecurity, body);
            if (got != expected)
                failures.Add($"[{count}] {kind}: 解密结果不符\n  C#  {got}\n  Py  {expected}");
        }

        Assert.True(count > 0, "向量文件里没有 decrypt_vectors");
        Assert.True(failures.Count == 0, "解密不一致：\n" + string.Join("\n", failures));
    }
}
