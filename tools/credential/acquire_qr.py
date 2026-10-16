#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
小米账号扫码登录 —— 获取直连 api.io.mi.com 所需的凭据（sid=xiaomiio）。

原理：
  复用 mijiaAPI 的二维码登录流程（全部走官方 account.xiaomi.com 端点），
  但把 sid 从 `mijia` 换成 `xiaomiio` —— 只有 xiaomiio 的服务令牌才能直连
  api.io.mi.com/app/*（与米家 App 同款，这正是我们的控制通道）。
  不用 mijiaAPI 的任何设备 API（它走第三方 api.mijia.tech 中转）。

输出：credentials.json（含密钥！导入到应用后请删除本文件）
  {userId, cUserId, ssecurity, serviceToken, passToken, deviceId, region, sid, source, capturedAt}

用法（用 venv-mijia 的解释器跑）：
  D:\\pccard-build\\venv-mijia\\Scripts\\python.exe acquire_qr.py --dry-run  # 只验证取二维码链路
  D:\\pccard-build\\venv-mijia\\Scripts\\python.exe acquire_qr.py            # 真登录：手机「米家」扫码
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).parent
QR_PNG = HERE / "login_qr.png"
LIB_AUTH = HERE / ".auth-xiaomiio.json"  # mijiaAPI 自己保存的登录态（含密钥，同上，用完删）


def build_api():
    from mijiaAPI import mijiaAPI

    class XiaomiIOAPI(mijiaAPI):
        """只改一处：把服务登录的 sid 换成 xiaomiio。"""

        def __init__(self, auth_data_path=None):
            super().__init__(auth_data_path)
            self.service_login_url = (
                "https://account.xiaomi.com/pass/serviceLogin"
                f"?_json=true&sid=xiaomiio&_locale={self.locale}"
            )

    return XiaomiIOAPI(str(LIB_AUTH))


def main() -> int:
    ap = argparse.ArgumentParser(description="米家扫码登录 → credentials.json（sid=xiaomiio）")
    ap.add_argument("--out", default=str(HERE / "credentials.json"))
    ap.add_argument("--dry-run", action="store_true", help="只取二维码不等待扫码")
    ap.add_argument("--rounds", type=int, default=3, help="扫码超时后重发二维码的轮数")
    args = ap.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    import qrcode

    api = build_api()

    auth = None
    for rnd in range(1, args.rounds + 1):
        data = api._get_qr_login_data()
        if data.get("refreshed"):
            print("[+] 本地已有有效登录态（token 刷新成功），直接使用")
            auth = api.auth_data
            break

        login_url = data["loginUrl"]
        png = QR_PNG
        try:
            qrcode.make(login_url).save(png)
        except PermissionError:  # 看图器还占着旧文件
            import time as _t

            png = HERE / f"login_qr_{int(_t.time())}.png"
            qrcode.make(login_url).save(png)
        print(f"[+] 二维码已生成：{png}")
        if data.get("qr"):
            print(f"    备选图链：{data['qr']}")
        try:
            os.startfile(str(png))  # 用系统看图器打开，供手机扫描
        except OSError as e:
            print(f"[!] 自动打开失败（{e}），请手动打开上面的 PNG 文件")
        print("[*] 请用手机「米家」App 扫一扫 → 确认登录（120 秒内）")

        if args.dry_run:
            print("[dry-run] 链路验证完成，不等待扫码。")
            return 0

        try:
            auth = api._complete_qr_login(data)
            break
        except Exception as e:  # noqa: BLE001
            print(f"[!] 第 {rnd}/{args.rounds} 轮扫码失败：{e}")
            if rnd == args.rounds:
                print("[x] 多轮均失败，请重试或改走浏览器 DevTools 方案（docs/runbook-credential.md）")
                return 1
            print("[*] 重新生成二维码...")

    assert auth is not None
    cred = {
        "userId": str(auth.get("userId", "")),
        "cUserId": str(auth.get("cUserId", "")),
        "ssecurity": auth.get("ssecurity", ""),
        "serviceToken": auth.get("serviceToken", ""),
        "passToken": auth.get("passToken", ""),
        "deviceId": auth.get("deviceId", ""),
        "ua": auth.get("ua", ""),
        "pass_o": auth.get("pass_o", ""),
        "region": "cn",
        "sid": "xiaomiio",
        "source": "qr",
        "capturedAt": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    }
    missing = [k for k in ("userId", "ssecurity", "serviceToken") if not cred[k]]
    if missing:
        print(f"[!] 登录态里缺字段 {missing}；auth_data 现有键：{sorted(auth)}")
        return 1

    Path(args.out).write_text(json.dumps(cred, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[✓] 凭据已写入 {args.out}")
    print(
        f"    userId={cred['userId']}  ssecurity={'有' if cred['ssecurity'] else '无'}"
        f"  serviceToken={'有' if cred['serviceToken'] else '无'}"
        f"  passToken={'有' if cred['passToken'] else '无'}"
        f"  cUserId={'有' if cred['cUserId'] else '无'}"
    )
    print("[!] 该文件含密钥：验证通过后导入应用/保险库，然后删除本文件与 .auth-xiaomiio.json。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
