using System.Net.Sockets;

namespace PcCard.Core.Probe;

/// <summary>
/// 旁路信号：阿里云同时是 frps，家里 PC 的 RDP 经 frp 映射到它的 127.0.0.1:33389。
/// frpc 在线 ⇔ 该端口能建连 —— 用作「电脑疑似开机」的启发式提示。
///
/// ★ 已知失效模式（README 里必须写清）：frpc 跑在别的机器上、PC 开着但 frpc 挂了、
///   PC 正在关机端口未释放、remotePort 被其它 frpc 抢占。
///   所以它只做界面上的「旁路」标注，绝不参与任何决策。
/// </summary>
public static class RdpTunnelProbe
{
    /// <summary>解析 "host:port"（默认 127.0.0.1:33389）；解析失败返回默认值。</summary>
    public static (string Host, int Port) ParseEndpoint(string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var parts = endpoint.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var p) && p is > 0 and < 65536)
                return (parts[0], p);
        }
        return ("127.0.0.1", 33389);
    }

    public static async Task<bool> IsEndpointOpenAsync(string? endpoint, int timeoutMs = 2000)
    {
        var (host, port) = ParseEndpoint(endpoint);
        return await IsTunnelOpenAsync(host, port, timeoutMs).ConfigureAwait(false);
    }

    public static async Task<bool> IsTunnelOpenAsync(
        string host = "127.0.0.1", int port = 33389, int timeoutMs = 2000)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != connect)
            {
                return false; // 超时
            }
            await connect.ConfigureAwait(false); // 抛出可能的异常
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
