# 阿里云部署与运维 Runbook

> 目标机：`<你的服务器IP>`（Windows Server 2022）· `ssh ali`
> 作用：常驻的「开机卡控制台」——任何能 RDP 到阿里云的地方都能给家里电脑开关机。

## 部署了什么

| 项 | 位置 |
|---|---|
| 程序 | `C:\Program Files\PcCard\`（PcCardApp.exe 界面+托盘 / pccard.exe 任务执行器 / 自包含运行时） |
| 数据 | `C:\ProgramData\PcCard\`（credentials.bin 凭据 · devices.json · tasks.json + tasks-index.txt · operations\ · logs\） |
| 托盘自启 | 计划任务 `PcCard\Tray`（onlogon + /RL HIGHEST + /IT，用户 Admin） |
| 定时任务 | 计划任务 `PcCard\Task_<id>`（SYSTEM 身份，无人登录也执行） |
| 公网使用 | RDP 进服务器 → 打开「开机卡控制台」（桌面/开始菜单） |

## 部署 / 升级流程

```powershell
# ① 本机构建（Release）
cd <你的路径>\pccard
.\tools\build.ps1 -Configuration Release -Stage
.\tools\make-installer.ps1 -Version 0.1.0

# ② 上传 + 安装（先杀进程，避免文件占用）
scp "build\installer\PcCard-Setup-0.1.0.exe" ali:C:/Windows/Temp/
ssh ali 'taskkill /f /im PcCardApp.exe & schtasks /end /tn "PcCard\Tray"'
ssh ali 'C:\Windows\Temp\PcCard-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=C:\Windows\Temp\pccard-install.log'

# ③ 验证
ssh ali '"C:\Program Files\PcCard\pccard.exe" doctor'
```

## 远程 GUI 验证（无显示器也行）

SSH 会话在 Session 0，GUI 起不来 —— 用**一次性计划任务**把自检丢进交互会话：

```powershell
ssh ali 'schtasks /Create /TN "PcCard\SelfCheck" /TR "\"C:\Program Files\PcCard\PcCardApp.exe\" --selfcheck --png C:\Windows\Temp\pc-selfcheck.png --timeout 30" /SC ONCE /ST 23:59 /RU Admin /RL HIGHEST /IT /F'
ssh ali 'schtasks /Run /TN "PcCard\SelfCheck"'
# 等 C:\Windows\Temp\pc-selfcheck.png.txt 出现（自检在断开态 RDP 会话里也能出图 —— RenderTargetBitmap 走 XAML 合成，不依赖桌面显示）
scp ali:C:/Windows/Temp/pc-selfcheck.png .
ssh ali 'schtasks /Delete /TN "PcCard\SelfCheck" /F'
```

## 日常运维

- **改凭据**：`Get-Content ... | ssh ali '...pccard.exe credential import --stdin'`
- **切设备**：`pccard.exe device list|select <did>`
- **命令行开关机**：`pccard.exe power on|off`；**状态**：`pccard.exe status [--json]`
- **定时任务**：界面里 CRUD（走 schtasks XML，UTF-16LE+BOM）；`pccard.exe task list|add|rm`

## 重启行为（重要）

- ✅ `PcCard\Task_*`（SYSTEM 计划任务）：**重启后照常工作**，无人登录也能执行
- ⚠️ 托盘 `PcCard\Tray`：只在**用户登录时**启动 —— 服务器重启后需 RDP 登录一次才会出现托盘图标
  （这是 Windows 的固有行为；定时任务不受影响。刻意不配自动登录——安全代价不值）

## 已知坑与排查

| 症状 | 处置 |
|---|---|
| 界面亮红「凭据失效」 | 云返回 401 → 重新获取并导入凭据（`runbook-credential.md`） |
| `doctor` 云端不通 | `api.io.mi.com:443` 出站被挡 / 服务器网络异常 |
| 旁路信号误报 | 33389 是 frps 端口：PC 开着但 frpc 崩了/PС 正关机/端口被别的 frpc 抢注都会失真 —— 仅参考，不参与决策 |
| 定时任务没触发 | `schtasks /Query /TN "PcCard\Task_<id>" /V` 看 Last Run Result；`pccard.exe task list` 看 tasks.json 侧 |
| 安装器报错 | 日志 `C:\Windows\Temp\pccard-install.log`（[Code] 段的 schtasks 操作都有 Log 记录） |

## 卸载

```powershell
ssh ali 'taskkill /f /im PcCardApp.exe'
ssh ali '"C:\Program Files\PcCard\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
```
卸载会删 `PcCard\Tray` 与 `tasks-index.txt` 列出的全部 `PcCard\Task_*`；
`C:\ProgramData\PcCard` 默认保留（凭据/日志），加 `/TASKS` 或手动删除可清。
