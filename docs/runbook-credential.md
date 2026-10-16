# 云凭据获取 Runbook

控制通道直连 `api.io.mi.com`，需要 **sid=xiaomiio** 的一组凭据：
`userId / cUserId / ssecurity / serviceToken / passToken`（+ deviceId/ua 辅助）。

> 为什么不能用米家 App 里的现成登录态：App 数据里这些全是加密态（passport SDK / AccountManager），
> 实测抠不出来。所以凭据获取是一次**独立的一次性动作**，与"已登录的 App"无关。

## 路线 P1（首选）：手机米家扫码登录

```powershell
# 首次先装依赖（独立 venv，不污染系统 python）
python -m venv D:\pccard-build\venv-mijia
D:\pccard-build\venv-mijia\Scripts\python.exe -m pip install mijiaAPI qrcode pillow

# 真登录：会弹出二维码 PNG，手机「米家」App 扫一扫 → 手机确认（120 秒/轮，共 3 轮）
D:\pccard-build\venv-mijia\Scripts\python.exe pccard\tools\credential\acquire_qr.py
# 产物：pccard\tools\credential\credentials.json    ← 含密钥，导入后删！
```

原理：复用 mijiaAPI 的二维码登录流程（官方 account.xiaomi.com 端点），只把 `sid` 换成 `xiaomiio`
（实现见脚本内 `XiaomiIOAPI` 子类；不用该库的任何设备 API）。

## 路线 P2（兜底，零不确定性）：浏览器 DevTools 抓取

1. 浏览器登录 `https://account.xiaomi.com/pass/serviceLogin?sid=xiaomiio&_json=true`（完成 2FA）
2. F12 → Network：
   - 搜 `serviceLoginAuth2` 的响应头 `extension-pragma` → **ssecurity**
   - 同响应 Set-Cookie → **passToken**、**cUserId**
   - 搜 `sts.api.io.mi.com` 响应 Set-Cookie → **serviceToken**
   - 响应体 / URL 里取 **userId**
3. 写成 credentials.json（字段见下），继续下面的验证

> 社区同款做法（PiotrMachowski/Xiaomi-cloud-tokens-extractor 2FA 变通）；两分钟的事。

## 验证（拿到即测）

```powershell
python pccard\tools\wsa\cloud_smoke.py get-list
# 期望：设备列表里有你的开机卡且 online=True
```

## 导入应用

```powershell
# 本机（开发机）
pccard.exe credential import <credentials.json>
# 服务器（走 stdin 管道，避免明文进命令行/事件日志）
Get-Content -Raw -Encoding UTF8 credentials.json | ssh ali '"C:\Program Files\PcCard\pccard.exe" credential import --stdin'
```

导入后凭据经 **DPAPI(LocalMachine)** 加密存 `C:\ProgramData\PcCard\credentials.bin`（仅本机可解）。

## credentials.json 字段

```json
{
  "userId": "1234567890", "cUserId": "…", "ssecurity": "…",
  "serviceToken": "…", "passToken": "…", "deviceId": "…",
  "ua": "…（登录时的 UA，保持会话一致）", "pass_o": "…",
  "region": "cn", "sid": "xiaomiio", "source": "qr|devtools|manual",
  "capturedAt": "2026-10-11T03:25:32+08:00"
}
```

## 失效与续期

- 症状：`doctor`/界面报 `AuthInvalid`（云返回 401）→ 重新走 P1/P2 导入即可
- `passToken` 已随凭据保存：将来可做免扫码的静默续期（`GET /pass/serviceLogin?sid=xiaomiio`
  携带 passToken → 跟随 location → 取新 serviceToken），目前未实现
- 换密/异地登录风控会立即作废 serviceToken —— 属预期，重导即可
