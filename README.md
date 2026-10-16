# PcCard — 开机卡控制台

在阿里云（Windows Server）上常驻的「米家电脑开机卡」控制台：**RDP 到任何地方都能给家里电脑开关机**。

- 形态：**未打包的 WinUI 3 应用**（`WindowsPackageType=None`，自包含），发布为 **exe 安装器**（Inno Setup，绝不用 MSIX）
- 控制通道：直连小米云 `api.io.mi.com`（ENCRYPT-RC4 签名），设备 = PZG 开机卡（Basic 档）
- 操作：开机 / 关机 / **重启** + **强制勾选**（关机→强制关机、重启→强制重启，写 `piid5=3/2`）——后两者官方插件对 Basic 档隐藏，按用户要求开放，属**实验性**，云端不支持时会明确报错
- 设备设置（⚙）：**童锁**（锁机箱物理电源键）/ **指示灯** / **通电自动开机**（停电恢复自动开机）——原 App 设置页功能，协议层全支持（实测通过）
- 交互保护：**点击动作后按钮全禁**，待刷新确认状态一致才解锁（防连按——`piid1` 为"按键动作"语义，连点=连按电源键）；启动/刷新自动从云端同步设备名称
- 三进程：`PcCardApp.exe`（界面 + 纯 P/Invoke 托盘）/ `pccard.exe`（无 UI 任务执行器，SYSTEM 计划任务入口）

## 目录速查

```
src/PcCard.Core/       UI 无关核心（云客户端/设备语义/DPAPI 凭据库/任务存储/schtasks 封装）
src/PcCard.Cli/        pccard.exe（doctor / status / power / task run …）
src/PcCard.App/        WinUI 3 界面 + 托盘 + --selfcheck 自渲染截图
tests/                 跨语言签名向量 + 任务 XML 单测
tools/wsa/             设备提取 / 云冒烟 / 向量生成（python）
tools/credential/      扫码获取云凭据（python + venv-mijia）
tools/                 build.ps1 / make-installer.ps1 / collect-crt.ps1 / verify-deps.ps1
installer/pccard.iss   Inno Setup 脚本
docs/                  协议侦察 / 凭据 runbook / 阿里云运维 runbook / UI 设计规范（Kimi）
```

## 构建

```powershell
.\tools\build.ps1 -Configuration Release -Stage -Test   # 编译 + 单测 + 汇集 + 依赖门禁
.\tools\make-installer.ps1 -Version 0.1.0               # → build\installer\PcCard-Setup-0.1.0.exe
```

> 环境前提：SDK 在 `D:\dotnet-sdk`（PATH 上的 dotnet 无 SDK）、`NUGET_PACKAGES=D:\nuget-packages`、
> Inno Setup 6 已装；含中文的脚本/资源文件一律 UTF-8 **带 BOM**。

## 文档

- [协议侦察记录](docs/protocol-findings.md) — 设备属性表 / 能力档 / 云接口实测
- [云凭据获取](docs/runbook-credential.md) — 扫码 / DevTools 两条路线
- [阿里云部署与运维](docs/runbook-aliyun.md) — 部署 / 远程自检 / 重启行为 / 排查
- [UI 设计规范](docs/ui-design.md) — Kimi 出品（Design Tokens 在此查阅）
