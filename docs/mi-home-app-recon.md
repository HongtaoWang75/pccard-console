# 米家 App 逆向侦察报告 —— 「能不能拿到更高级的权限？」

> 日期：2026-10-20 · 目标：`com.xiaomi.smarthome` 11.8.705（versionCode 110817051，取自已授权的 Redmi K90 Max；WSA 内为 11.8.703）
> 方法：纯静态（dex 类清单 28,145 类 + JADX 定向反编译 + 公开 miot spec 对照 + 插件 bundle 全量核对），**未执行 App、未改设备**。

## 结论（先看这里）

1. **设备协议层没有「更高档位」可解锁。** 公开 miot spec（`urn:miot-spec-v2:device:outlet:0000A002:pzg-1127:1:0000D030`）全量只有：
   - siid1 Device Information（manufacturer/model/serial/firmware，只读）
   - siid2 switch：piid1 开关、piid5 重启/强制枚举、piid6 设备类型（Basic/…）、piid7/8/9 童锁/指示灯/通电自启
   - aiid1 `toggle` 动作（与 piid1 等价的切换）
   插件 bundle 全量核对同样只用 1/5/6/7/8/9 —— **这就是全部**；Basic/Standard/Profession 只影响官方插件菜单显隐，不是协议档位。控制台已把能控的全部实现。
2. **插件框架的「原始调用」通道即我们已在用的云 API。** RN 插件可调 `MIOTDeviceModuleCore.callMethod / callMethodFromCloud / callMethodFromLocal`（通用方法调用）与 `DeviceController.doAction/getPropertyValue` —— 与 `miotspec prop/get|set` 同一条路，无提权绕行。
3. **存在 App 内「开发者 AB 配置页」`ABConfigDevelopActivity`**（增/改/启停 ABConfig 客户端实验开关）+ `abtest` 存储类。属**客户端功能开关**（可能藏隐藏 UI），不改变设备权限。
4. **内置 device_config（assets/device_config/plugin_config_cn-zh_cn，17MB JSON）不含本卡条目** —— 插件按需从云下发，无额外能力描述。
5. **唯一可能更丰富的通道 = 本地 miIO（54321，token 已知）**：vendor 私有方法可能超出 miot spec，但本机（家里有线网 192.168.1.x）与卡片（WiFi 子网 192.168.2.x）不通。若网络打通可枚举方法名（预期仍是同一组属性）。

## 副产物（实际收益）

- **会话保活**：`mijiaAPI` 的本地登录态（`tools/credential/.auth-xiaomiio.json`）可**静默刷新 serviceToken**（2026-10-20 实测：`acquire_qr.py` 直接“刷新Token成功，无需登录”）。手写的 `serviceLoginAuth2` + passToken 流程会被小米风控拒（`21315 无效的sid`；社区结论：纯 HTTP 续期已封，主流做法是真实浏览器会话或二维码）。
- **运维建议**：家里 PC 上建每日计划任务跑 `acquire_qr.py` 刷新分支 → 成功后 scp 到阿里云并 `credential import --stdin`，实现免扫码长期保活（家庭 PC 不开机时跳过，令牌通常可撑数天）。

## 可继续的安全探针（预期无新能力）

| 探针 | 说明 |
|---|---|
| 云侧 `aiid1 toggle` | 与 piid1 等价的切换动作（cloud_smoke action 子命令） |
| 本地 miIO | `miIO.info` / `get_prop` / `set_prop` 等 —— 需 192.168.2.x 可达 |
| App 开发者 AB 页 | 需找到入口（未见直接 UI 暴露，可能依赖 AB/登录态）；风险=客户端行为变化，与设备权限无关 |

## 取证文件

- 类清单：`%TEMP%\mihome-apk\scan\classes.txt`（28,145 类）、`suspicious.txt`
- 反编译样本：`%TEMP%\mihome-apk\jadx-out\sources\...`（ABConfigDevelopActivity / MIOTDeviceModuleCore / DeviceController）
- 公开 spec：<https://miot-spec.org/miot-spec-v2/instance?type=urn:miot-spec-v2:device:outlet:0000A002:pzg-1127:1:0000D030>
