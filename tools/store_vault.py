#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 credentials.json 里的米家云凭据一次性写入保险库（vault.py）。

主密码只弹 **一次** Windows 系统凭据窗口（vault.secure_password_dialog），
比逐个 `vault.py -i set` 弹 N 次省事。

用法：python pccard/tools/store_vault.py [credentials.json 路径]
写入的键：
    mijia.userid / mijia.cuserid / mijia.ssecurity / mijia.servicetoken /
    mijia.passtoken / mijia.ua / mijia.device.did / mijia.device.token
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]  # 即 vault.py 所在目录（本脚本位于 <repo>/tools/ 下）
sys.path.insert(0, str(REPO))

import vault  # noqa: E402  （复用保险库的加解密与凭据窗）

DEVICES = Path(__file__).parent.parent / "tools" / "wsa" / "devices.json"
DEFAULT_CRED = Path(__file__).parent.parent / "tools" / "credential" / "credentials.json"


def main() -> int:
    cred_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_CRED
    cred = json.loads(cred_path.read_text(encoding="utf-8"))

    # 目标设备的本地控制 token 也一并存（将来本地通道备用）——填入你要保存的 did
    target_did = ""
    dev_token = ""
    if DEVICES.exists():
        doc = json.loads(DEVICES.read_text(encoding="utf-8"))
        for d in doc.get("devices", []):
            if str(d.get("did")) == target_did and d.get("token"):
                dev_token = d["token"]

    if not vault.VAULT_FILE.exists():
        print(f"[x] 找不到保险库：{vault.VAULT_FILE}", file=sys.stderr)
        return 1

    master = vault.secure_password_dialog("PcCard 凭据入库 —— 输入保险库主密码")
    vf = vault.read_vault_file()
    try:
        plaintext = vault.decrypt(vf, master)
    except Exception:
        print("[x] 主密码错误（或保险库损坏）", file=sys.stderr)
        return 2

    data = json.loads(plaintext)
    to_store = {
        "mijia.userid": cred.get("userId", ""),
        "mijia.cuserid": cred.get("cUserId", ""),
        "mijia.ssecurity": cred.get("ssecurity", ""),
        "mijia.servicetoken": cred.get("serviceToken", ""),
        "mijia.passtoken": cred.get("passToken", ""),
        "mijia.ua": cred.get("ua", ""),
        "mijia.device.did": target_did,
        "mijia.device.token": dev_token,
    }
    for k, v in to_store.items():
        if v:
            vault.set_nested(data, k, v)

    new_vault = vault.encrypt(json.dumps(data, indent=2, ensure_ascii=False).encode("utf-8"), master)
    vault.write_vault_file(new_vault)
    if vault.SESSION_FILE.exists():
        vault.write_session(data)

    stored = [k for k, v in to_store.items() if v]
    print(f"[✓] 已写入保险库 {len(stored)} 个键：")
    for k in stored:
        print(f"    {k}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
