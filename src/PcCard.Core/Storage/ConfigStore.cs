using PcCard.Core.Cloud;
using PcCard.Core.Models;

namespace PcCard.Core.Storage;

/// <summary>运行状态（当前选中的设备等）。</summary>
public sealed class AppState
{
    public string? SelectedDid { get; set; }
    public string? LastSyncUtc { get; set; }

    /// <summary>
    /// 旁路探测端点（frp 隧道口）。
    /// 服务器上 frps 监听 127.0.0.1:33389（默认）；本机开发时改成 <你的公网服务器IP>:33389。
    /// 只影响界面上的"旁路"提示，不参与任何决策。
    /// </summary>
    public string TunnelEndpoint { get; set; } = "127.0.0.1:33389";
}

/// <summary>设备清单与选中状态的读写。</summary>
public static class ConfigStore
{
    // ── 运行状态 ──

    public static AppState LoadState() => JsonFile.Read<AppState>(AppPaths.StatePath) ?? new AppState();

    public static void SaveState(AppState state) => JsonFile.Write(AppPaths.StatePath, state);

    // ── 设备清单 ──

    public static DeviceCatalogDocument? LoadDevices() => JsonFile.Read<DeviceCatalogDocument>(AppPaths.DevicesPath);

    /// <summary>导入提取脚本产出的 devices.json（先解析校验，再原子落盘）。</summary>
    public static DeviceCatalogDocument ImportDevices(string sourceJsonPath)
    {
        var doc = JsonFile.Read<DeviceCatalogDocument>(sourceJsonPath)
                  ?? throw new InvalidOperationException($"无法解析设备清单：{sourceJsonPath}");
        if (doc.Devices.Count == 0)
            throw new InvalidOperationException("设备清单为空");

        File.Copy(sourceJsonPath, AppPaths.DevicesPath, overwrite: true);
        return doc;
    }

    /// <summary>
    /// 用云端设备清单回写本地目录（名称/型号/本地 IP/token/在线状态）。
    /// ★ 米家里改了设备名，控制台不需要重新提取 devices.json —— 每次刷新自动跟上。
    /// 以 Did 对齐；云端有的更新，本地独有的字段/设备保留（不做删除）。
    /// 返回是否有内容变化（调用方可据此决定要不要重建界面）。
    /// </summary>
    public static bool SyncFromCloud(IEnumerable<CloudDevice> cloudDevices)
    {
        var catalog = LoadDevices();
        if (catalog is null || catalog.Devices.Count == 0) return false;

        var changed = false;
        foreach (var cd in cloudDevices)
        {
            if (string.IsNullOrEmpty(cd.Did)) continue;
            var local = catalog.Devices.FirstOrDefault(d => d.Did == cd.Did);
            if (local is null) continue;

            if (!string.IsNullOrEmpty(cd.Name) && local.Name != cd.Name) { local.Name = cd.Name; changed = true; }
            if (!string.IsNullOrEmpty(cd.Model) && local.Model != cd.Model) { local.Model = cd.Model; changed = true; }
            if (!string.IsNullOrEmpty(cd.LocalIp) && local.LocalIp != cd.LocalIp) { local.LocalIp = cd.LocalIp; changed = true; }
            if (!string.IsNullOrEmpty(cd.Token) && local.Token != cd.Token) { local.Token = cd.Token; changed = true; }
            if (local.IsOnline != cd.IsOnline) { local.IsOnline = cd.IsOnline; changed = true; }
        }

        if (changed) JsonFile.Write(AppPaths.DevicesPath, catalog);
        return changed;
    }

    public static DeviceRecord? GetSelectedDevice()
    {
        var catalog = LoadDevices();
        if (catalog is null || catalog.Devices.Count == 0) return null;
        var state = LoadState();
        return catalog.Devices.FirstOrDefault(d => d.Did == state.SelectedDid) ?? catalog.Devices[0];
    }

    public static void SetSelectedDid(string did)
    {
        var state = LoadState();
        state.SelectedDid = did;
        SaveState(state);
    }
}
