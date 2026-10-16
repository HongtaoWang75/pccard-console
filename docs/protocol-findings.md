# 开机卡协议侦察记录

> 2026-10-11 · 来源：WSA 内米家 App v11.8.703 数据 + RN 插件 bundle 逆向 + 云端实测

## 设备

| 项 | 值 |
|---|---|
| 型号 | `pzg.plug.1127`（PZG 智能插座形态的开机卡；spec urn: `urn:miot-spec-v2:device:outlet:0000A002:pzg-1127:1:0000D030`） |
| 目标机 | 「XXX的电脑开机卡」 did `1234xxxxxx`（uid 已省略） |
| 另一台 | 「YYY电脑开机卡」 did `2345xxxxxx`（实测时长期离线） |
| 网络 | 192.168.2.x（Xiaomi_17 WiFi 子网）—— 与家里有线网 192.168.1.x 不通，本地 miIO 用不上 |
| 本地 token | 明文 32 hex，存于米家 App `files/mmkv/mmkv_id_local_control`（`key_local_control_cache_devices` 的 JSON 数组），保留备用 |

## 属性表（siid 2）

| piid | 类型 | 语义 | 来源 |
|---|---|---|---|
| 1 | bool | 开关机 —— **★ 写入 = 按一下电源键（动作语义！每次写都按一下，与值无关；连点=连按）**；读取反映当前开/关机状态 | 真机实测（含"值未变仍触发"的案例） |
| 5 | uint8 w | `1`=重启 `2`=强制重启 `3`=强制关机（协议全接受，Basic 仅官方 UI 隐藏） | 插件 `resetForcePower` + 实测接受 |
| 6 | string r | 设备类型档（见下） | 插件 `getDataFromDevice(6)` |
| 7 | bool rw | **童锁** — 打开后**机箱物理电源按钮不可用**（云端控制不受影响） | 插件设置页 `sendSpec(7,…)`（原文案"默认关闭，打开后机箱按钮不可用"）+ 实测读写 |
| 8 | bool rw | **指示灯** — 关闭后设备 LED 熄灭 | 插件 `sendSpec(8,…)` + 实测读写 |
| 9 | bool rw | **通电自动开机** — 设备恢复供电后自动给电脑开机（停电恢复场景） | 插件 `sendSpec(9,…)` + 实测读写 |

## 设备类型档与能力门禁（来自插件 `xiaomi.kjkcommon.mtk1` 逻辑）

```
Profession / Standard → 显示重启菜单（重启/强制重启/强制关机）+ 童锁/来电自启开关
Classic              → 显示简化重启菜单；童锁/来电自启隐藏
Basic                → hideRebootOption()：重启菜单隐藏   ← 我们的卡是 Basic
```

- **本卡实测 `piid6 == "Basic"` → 只做 开/关机**（`piid1`），强制类不提供（与官方插件行为一致）
- 插件轮询节奏：开机态 2s / 关机态 25s；UI 主色 `#017D7B`（开）/ `#707E87`（关）

## 云端接口（已实测跑通）

- 基址 `https://api.io.mi.com/app/*`，签名 = ENCRYPT-RC4（mijicloud/micloud 同款，`MiCloudSigner.cs` / `cloud_smoke.py` 双实现互验）
- 用到的端点：
  - `POST /home/device_list` —— 设备清单（did/model/name/isOnline/localip/token）
  - `POST /miotspec/prop/get` —— `{"params":[{did,siid,piid}...]}`，单属性级 code
  - `POST /miotspec/prop/set` —— 写值，开机 = `{siid:2,piid:1,value:true}`
- 实测错误码：
  - `401` —— 签名/登录态失效（需要重新导入凭据）
  - `-704042011` —— 设备无响应（离线设备的属性读取；-704 一族归为设备侧错误）
  - 不存在的 did —— 返回设备侧错误（写入被拒）
- 离线设备的 `piid1` 仍可读（云端缓存值），`piid6` 报 -704042011 —— 界面上的在线/离线以 `device_list.isOnline` 为准

## 提取链路（可复现）

```bash
# 设备清单（含 token）—— 全程只读
python tools/wsa/extract_devices.py
# 旁路文件（调试）：
#   files/mmkv/smart.widget.base.data.info（widget 缓存，也含两台卡）
#   files/datastore/own_home_device_33_v2.parcel（UTF-16LE 字符串交叉验证）
# 插件 bundle（协议金矿，132KB）：
#   files/plugin/install/rn/1027164/1042255/android/main.bundle
```

## 已知边界

- 云凭据（ssecurity/serviceToken）在 App 里是加密态，**抠不出来** —— 走扫码/浏览器抓取（见 `runbook-credential.md`）
- `piid5`（重启/强制档）与 `piid7/8/9`（设置开关）在 Basic 档的官方插件里被隐藏，但**协议层全接受**（实测读写通过）—— 控制台按用户要求全部开放（实验性）
- **`piid1` 是按键动作语义**：连写 = 连按电源键（会打断刚启动的机器）→ 控制台的"点击后禁用直至状态确认"机制是硬性防连按保护
- 本地 miIO（token 已拿到）因跨网段暂不可用；将来若网络打通可作云通道的回退
