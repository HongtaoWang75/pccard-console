using System.Net.Http;
using System.Text;
using System.Text.Json;
using PcCard.Core.Models;

namespace PcCard.Core.Cloud;

/// <summary>
/// 直连小米云 api.io.mi.com/app/* 的客户端（签名见 <see cref="MiCloudSigner"/>）。
/// 只实现用得到的接口：device_list / miotspec prop get&amp;set / action。
/// </summary>
public sealed class MiCloudClient : IDisposable
{
    public const string BaseUrl = "https://api.io.mi.com/app";

    private readonly HttpClient _http;
    private readonly Credentials _cred;

    public MiCloudClient(Credentials cred, HttpMessageHandler? handler = null)
    {
        _cred = cred;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>发一个签名请求，返回解析后的 JSON（已处理 RC4/gzip）。</summary>
    public async Task<JsonDocument> PostAsync(string uri, object dataObj, CancellationToken ct = default)
    {
        var dataJson = JsonSerializer.Serialize(dataObj, CompactJson.Options);
        var nonce = MiCloudSigner.GenNonce();
        var sn = MiCloudSigner.SignedNonce(_cred.Ssecurity, nonce);
        var form = MiCloudSigner.BuildEncryptedForm(uri, dataJson, nonce, sn, _cred.Ssecurity);

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + uri);
        req.Content = new FormUrlEncodedContent(form);
        req.Headers.TryAddWithoutValidation("User-Agent", BuildUserAgent());
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Headers.TryAddWithoutValidation("miot-accept-encoding", "GZIP");
        req.Headers.TryAddWithoutValidation("miot-encrypt-algorithm", "ENCRYPT-RC4");
        req.Headers.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        req.Headers.TryAddWithoutValidation("Cookie", BuildCookie());

        HttpResponseMessage resp;
        string raw;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MiCloudException(MiCloudErrorKind.NetError, 0, ex.Message);
        }

        // 明文 JSON 直接收（少见）；否则 base64 密文 → RC4 解密
        string plain;
        if (raw.TrimStart().StartsWith('{'))
        {
            plain = raw;
        }
        else
        {
            try
            {
                plain = MiCloudSigner.DecryptResponse(nonce, _cred.Ssecurity, raw);
            }
            catch (Exception ex)
            {
                throw new MiCloudException(MiCloudErrorKind.Protocol, 0,
                    $"响应解密失败：{ex.Message}；原文前 120 字符：{Trunc(raw)}");
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(plain);
        }
        catch (JsonException ex)
        {
            throw new MiCloudException(MiCloudErrorKind.Protocol, 0,
                $"响应不是 JSON：{ex.Message}；原文前 120 字符：{Trunc(plain)}");
        }

        var code = doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
        if (code != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString()
                : doc.RootElement.TryGetProperty("desc", out var d) ? d.GetString()
                : "(无 message)";
            doc.Dispose();
            throw MiCloudException.FromCode(code, msg ?? "");
        }
        return doc;
    }

    /// <summary>设备清单（含 token/online/localip）。用于验证凭据 + 界面设备列表。</summary>
    public async Task<List<CloudDevice>> GetDeviceListAsync(CancellationToken ct = default)
    {
        var doc = await PostAsync("/home/device_list",
            new Dictionary<string, object> { ["getVirtualModel"] = false, ["getHuamiDevices"] = 1 }, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            var list = new List<CloudDevice>();
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    list.Add(new CloudDevice
                    {
                        Did = GetStr(item, "did"),
                        Name = GetStr(item, "name"),
                        Model = GetStr(item, "model"),
                        Token = GetStr(item, "token"),
                        LocalIp = GetStr(item, "localip"),
                        Mac = GetStr(item, "mac"),
                        IsOnline = item.TryGetProperty("isOnline", out var on) && on.ValueKind == JsonValueKind.True,
                    });
                }
            }
            return list;
        }
    }

    /// <summary>批读 miotspec 属性。</summary>
    public async Task<JsonDocument> PropGetAsync(string did, IReadOnlyList<(int Siid, int Piid)> props, CancellationToken ct = default)
    {
        var paramsArr = props.Select(p => new Dictionary<string, object>
        {
            ["did"] = did, ["siid"] = p.Siid, ["piid"] = p.Piid,
        }).ToArray();
        return await PostAsync("/miotspec/prop/get", new Dictionary<string, object> { ["params"] = paramsArr }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>写单个 miotspec 属性。</summary>
    public async Task<JsonDocument> PropSetAsync(string did, int siid, int piid, object value, CancellationToken ct = default)
    {
        var paramsArr = new[]
        {
            new Dictionary<string, object> { ["did"] = did, ["siid"] = siid, ["piid"] = piid, ["value"] = value },
        };
        return await PostAsync("/miotspec/prop/set", new Dictionary<string, object> { ["params"] = paramsArr }, ct)
            .ConfigureAwait(false);
    }

    private string BuildCookie()
    {
        var sb = new StringBuilder();
        sb.Append("cUserId=").Append(_cred.CUserId ?? _cred.UserId).Append(';');
        sb.Append(" serviceToken=").Append(_cred.ServiceToken).Append(';');
        sb.Append(" yetAnotherServiceToken=").Append(_cred.ServiceToken).Append(';');
        sb.Append(" userId=").Append(_cred.UserId).Append(';');
        sb.Append(" locale=zh_CN;");
        if (!string.IsNullOrEmpty(_cred.DeviceId))
            sb.Append(" PassportDeviceId=").Append(_cred.DeviceId).Append(';');
        return sb.ToString();
    }

    private string BuildUserAgent()
    {
        if (!string.IsNullOrEmpty(_cred.Ua)) return _cred.Ua!;
        var passO = _cred.PassO ?? RandomHex(16, "0123456789abcdef");
        static string R() => RandomHex(40, "0123456789ABCDEF");
        return $"Android-15-11.0.701-Xiaomi-23046RP50C-OS2.0.212.0.VMYCNXM-{R()}-CN-{R()}-{R()}-SmartHome-MI_APP_STORE-{R()}|{R()}|{passO}-64";
    }

    private static string RandomHex(int n, string alphabet)
    {
        var chars = new char[n];
        for (var i = 0; i < n; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120];

    public void Dispose() => _http.Dispose();
}

public sealed class CloudDevice
{
    public string? Did { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Token { get; set; }
    public string? LocalIp { get; set; }
    public string? Mac { get; set; }
    public bool IsOnline { get; set; }
}

/// <summary>紧凑 JSON（表单里 data 字段的序列化选项）。</summary>
internal static class CompactJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
