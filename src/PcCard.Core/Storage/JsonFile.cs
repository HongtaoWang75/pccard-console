using System.Text.Encodings.Web;
using System.Text.Json;
using PcCard.Core.Json;

namespace PcCard.Core.Storage;

/// <summary>JSON 文件读写小件：原子写（.tmp → 替换）+ 统一选项。</summary>
public static class JsonFile
{
    /// <summary>
    /// ★ 必须显式 UTF8Encoding(false)：.NET 的 <c>Encoding.UTF8</c> 静态实例默认**带 BOM**，
    /// 写出的文件会让 Python 等外部工具解析失败（实测踩过）。
    /// </summary>
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // tasks.json 等对外文件用 camelCase（Python 直读友好）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义
        Converters =
        {
            new FlexibleStringConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    private static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false,
    };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Options);
    }

    /// <summary>原子写：先写 .tmp 再替换，避免半截文件。</summary>
    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public static string ToCompactJson<T>(T value) => JsonSerializer.Serialize(value, CompactOptions);
}
