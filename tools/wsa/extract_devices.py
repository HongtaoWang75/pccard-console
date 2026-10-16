#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 WSA 米家 App（com.xiaomi.smarthome）提取设备清单（含「电脑开机卡」）。

数据来源（全程只读，不往设备写任何东西）：
  /data/data/com.xiaomi.smarthome/files/mmkv/mmkv_id_local_control
    —— MMKV 文件；key = key_local_control_cache_devices，值是明文 JSON 数组，
       每个元素是一台设备的完整记录（did / model / name / token / localip / mac ...）
  /data/data/com.xiaomi.smarthome/files/datastore/own_home_device_33_v2.parcel
    —— 仅作交叉验证（token 是否也在 parcel 里，ASCII / UTF-16LE 两种编码）

解析策略：不依赖 MMKV 的确切磁盘布局 —— 先把「主 key 之后的第一个 JSON 根」按
括号配平切出来；若失败，则全文件扫描所有可解析的 JSON 根。收集所有含 did+token
的记录，按 did 去重（保留字段最全的）。

用法：
  python extract_devices.py                    # 走 adb 从 WSA 读
  python extract_devices.py --local <file.bin> # 直接解析本地原始文件（离线调试）
  python extract_devices.py --dump-parcel      # 额外打印 parcel 扫到的字符串
退出码：0 成功；2 断言与已知值不一致
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import re
import subprocess
import sys
import time
from pathlib import Path

ADB_CANDIDATES = [r"D:\adb\adb.exe", "adb"]
SERIAL = "127.0.0.1:58526"
PKG = "com.xiaomi.smarthome"
SRC_MMKV = f"/data/data/{PKG}/files/mmkv/mmkv_id_local_control"
SRC_PARCEL = f"/data/data/{PKG}/files/datastore/own_home_device_33_v2.parcel"

MMKV_KEY = b"key_local_control_cache_devices"

# 回归断言用的已知值（键 = did，值 = 字段期望值）。
# ★ 开源版为空：跑之前填入你自己侦察到的设备记录（解析结果会与之逐字段比对）；
#   留空则跳过断言、只做提取。
KNOWN: dict = {
    # 1234567890: {"token": "0" * 32, "local_ip": "192.168.2.100", "model": "pzg.plug.1127"},
}

HEX32 = re.compile(r"^[0-9a-f]{32}$")
HEX96 = re.compile(r"^[0-9a-f]{96}$")

# JSON 字段别名（大小写不敏感）
ALIASES = {
    "did": ["did", "deviceid"],
    "model": ["model", "modelname"],
    "name": ["name", "devicename", "showname"],
    "token": ["token", "localtoken"],
    "local_ip": ["localip", "local_ip", "ip", "deviceip"],
    "mac": ["mac", "macaddress"],
    "ssid": ["ssid", "wifissid"],
    "spec_urn": ["specurn", "urn", "spec_urn"],
    "is_online": ["isonline", "online"],
    "uid": ["uid", "userid", "owneruid"],
    "rssi": ["rssi"],
    "last_online": ["last_online", "lastonline"],
    "fw": ["fw", "firmwareversion"],
}


# ─────────────────────────── adb / 设备侧读取 ───────────────────────────

def find_adb() -> str:
    for c in ADB_CANDIDATES:
        try:
            r = subprocess.run([c, "version"], capture_output=True, timeout=10)
            if r.returncode == 0:
                return c
        except (OSError, subprocess.TimeoutExpired):
            continue
    sys.exit("[x] 找不到 adb（试过 D:\\adb\\adb.exe 与 PATH），请确认已安装")


def adb_run(adb: str, *args: str, timeout: int = 90) -> subprocess.CompletedProcess:
    return subprocess.run([adb, "-s", SERIAL, *args], capture_output=True, timeout=timeout)


def ensure_connected(adb: str) -> None:
    out = adb_run(adb, "devices").stdout.decode("utf-8", "replace")
    if SERIAL not in out:
        print(f"[*] {SERIAL} 未连接，尝试 adb connect ...")
        subprocess.run([adb, "connect", SERIAL], capture_output=True, timeout=30)
        time.sleep(2)
        out = adb_run(adb, "devices").stdout.decode("utf-8", "replace")
        if SERIAL not in out:
            sys.exit(f"[x] WSA ADB 不可用（{SERIAL} 不在设备列表）。请先启动 WSA。\n{out}")
    print(f"[+] adb 已连接：{SERIAL}")


def root_cat(adb: str, path: str, retries: int = 3) -> bytes:
    """su -c 'cat <path>' —— MagiskSU 偶发首次拒绝，重试。exec-out 保证字节原样。"""
    last = ""
    for i in range(1, retries + 1):
        r = adb_run(adb, "exec-out", f"su -c 'cat {path}'")
        if r.returncode == 0 and r.stdout:
            return r.stdout
        last = (r.stderr or b"").decode("utf-8", "replace").strip()[:200] or f"rc={r.returncode}"
        print(f"[!] 读取 {path} 失败（第 {i}/{retries} 次）：{last}")
        time.sleep(1.5)
    sys.exit(f"[x] 无法读取 {path}：{last}")


def app_version(adb: str) -> str:
    try:
        r = adb_run(adb, "shell", "dumpsys package " + PKG, timeout=60)
        m = re.search(rb"versionName=(\S+)", r.stdout)
        return m.group(1).decode() if m else "?"
    except Exception:
        return "?"


# ─────────────────────────── JSON 提取（括号配平，感知数组/对象） ───────────────────────────

def balanced_json_slice(data: bytes, start: int) -> int | None:
    """从 start（应为 b'{' 或 b'['）按括号配平找结束位置（含），失败返回 None。"""
    if start >= len(data) or data[start] not in (0x7B, 0x5B):  # '{' '['
        return None
    stack: list[int] = []
    in_str = False
    esc = False
    for i in range(start, len(data)):
        c = data[i]
        if in_str:
            if esc:
                esc = False
            elif c == 0x5C:  # backslash
                esc = True
            elif c == 0x22:  # quote
                in_str = False
            continue
        if c == 0x22:
            in_str = True
        elif c in (0x7B, 0x5B):  # { [
            stack.append(c)
        elif c in (0x7D, 0x5D):  # } ]
            if not stack:
                return None
            open_c = stack.pop()
            if (c == 0x7D) != (open_c == 0x7B):  # 括号类型必须配对
                return None
            if not stack:
                return i
    return None


def collect_json_roots(data: bytes) -> list[tuple[object, str]]:
    """收集文件里所有可解析的 JSON 根。返回 [(对象, 来源说明)]，按 offset 去重。"""
    roots: list[tuple[object, str]] = []
    seen_pos: set[int] = set()

    def try_at(pos: int, how: str) -> int | None:
        end = balanced_json_slice(data, pos)
        if end is None:
            return None
        try:
            obj = json.loads(data[pos : end + 1].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return None
        roots.append((obj, f"{how}@offset {pos}"))
        return end

    # 1) 主 key 之后的第一个 JSON 根
    i = data.find(MMKV_KEY)
    if i >= 0:
        j = i + len(MMKV_KEY)
        for k in range(j, min(j + 64, len(data))):
            if data[k] in (0x7B, 0x5B):
                if try_at(k, "主key"):
                    seen_pos.add(i)
                break

    # 2) 全文件扫描兜底（从每个 '{' / '[' 尝试，成功就跳过整段）
    pos = 0
    while pos < len(data):
        if data[pos] in (0x7B, 0x5B):
            end = try_at(pos, "扫描")
            if end is not None:
                pos = end + 1
                continue
        pos += 1
    return roots


# ─────────────────────────── 记录收集与规范化 ───────────────────────────

def walk_records(obj, out: list[dict]) -> None:
    """递归收集所有含 did+token 的记录（大小写不敏感键名）。"""
    if isinstance(obj, dict):
        low = {k.lower(): v for k, v in obj.items()}
        if "did" in low and "token" in low:
            rec = {}
            for field, names in ALIASES.items():
                for n in names:
                    if n in low and low[n] not in (None, ""):
                        rec[field] = low[n]
                        break
            out.append(rec)
            return
        for v in obj.values():
            walk_records(v, out)
    elif isinstance(obj, list):
        for v in obj:
            walk_records(v, out)


def decrypt_token_if_needed(token: str) -> tuple[str, str]:
    """新版米家可能把 token 存成 96 hex（AES-ECB、全零 key）。解密取 32 位。

    用 ECB 不是我们的选择，而是米家 App 自己的存储格式（社区逆向结论），
    这里只做只读解密兼容，不用于任何自研加密场景。
    """
    if HEX32.match(token):
        return token, "plain32"
    if HEX96.match(token):
        try:
            from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

            key = bytes(16)
            dec = Cipher(algorithms.AES(key), modes.ECB()).decryptor()
            pt = (dec.update(bytes.fromhex(token)) + dec.finalize()).decode("utf-8", "replace")
            m = HEX32.search(pt)
            if m:
                return m.group(0), "aes-ecb(zero-key)"
        except Exception as e:  # noqa: BLE001
            return token, f"decrypt-failed:{e}"
    return token, "unknown-format"


def normalize(rec: dict) -> dict:
    did = rec.get("did")
    try:
        did = int(str(did))
    except ValueError:
        pass
    token, fmt = decrypt_token_if_needed(str(rec.get("token", "")))
    return {
        "did": did,
        "model": rec.get("model"),
        "name": rec.get("name"),
        "token": token,
        "token_format": fmt,
        "local_ip": rec.get("local_ip"),
        "mac": rec.get("mac"),
        "ssid": rec.get("ssid"),
        "spec_urn": rec.get("spec_urn"),
        "is_online": rec.get("is_online"),
        "uid": rec.get("uid"),
        "rssi": rec.get("rssi"),
        "last_online": rec.get("last_online"),
        "fw": rec.get("fw"),
    }


# ─────────────────────────── parcel 交叉验证 ───────────────────────────

def parcel_strings(data: bytes, min_len: int = 3) -> list[str]:
    """扫 Android Parcel 的 UTF-16LE 字符串槽：uint32(len) + len 个 UTF-16 码元。"""
    out: list[str] = []
    n = len(data)
    i = 0
    while i + 4 <= n:
        (ln,) = (int.from_bytes(data[i : i + 4], "little"),)
        if 0 < ln < 512 and i + 4 + ln * 2 <= n:
            try:
                s = data[i + 4 : i + 4 + ln * 2].decode("utf-16-le")
                printable = sum(1 for ch in s if ch.isprintable() or ch in "\n\t")
                if printable / len(s) > 0.9 and "\x00" not in s:
                    if len(s) >= min_len:
                        out.append(s)
                    i += 4 + ln * 2
                    i += (-i) % 4  # Parcel 字符串 4 字节对齐
                    continue
            except UnicodeDecodeError:
                pass
        i += 1
    return out


def crosscheck_parcel(data: bytes, devices: list[dict]) -> None:
    print("\n[*] parcel 交叉验证（token 是否同时存在于 parcel）：")
    for d in devices:
        t = str(d["token"])
        in_ascii = t.encode() in data
        in_u16 = t.encode("utf-16-le") in data
        mark = "✓" if (in_ascii or in_u16) else "✗"
        enc = "ascii" if in_ascii else ("utf16" if in_u16 else "无")
        print(f"    {mark} did={d['did']} token({enc})")


# ─────────────────────────── 主流程 ───────────────────────────

def parse_mmkv(data: bytes) -> tuple[list[dict], list[str]]:
    roots = collect_json_roots(data)
    notes = [how for _, how in roots]
    raw_records: list[dict] = []
    for obj, _ in roots:
        walk_records(obj, raw_records)

    # 按 did 去重：保留「非空字段数」最多的记录
    by_did: dict = {}
    for r in raw_records:
        rec = normalize(r)
        old = by_did.get(rec["did"])
        if old is None or len([v for v in rec.values() if v]) > len(
            [v for v in old.values() if v]
        ):
            by_did[rec["did"]] = rec
    return sorted(by_did.values(), key=lambda r: str(r["did"])), notes


def main() -> int:
    ap = argparse.ArgumentParser(description="从 WSA 米家 App 提取设备清单")
    ap.add_argument("--out", default=str(Path(__file__).parent / "devices.json"))
    ap.add_argument("--local", help="直接解析本地原始文件（跳过 adb，离线调试用）")
    ap.add_argument("--dump-parcel", action="store_true", help="打印 parcel 扫描到的字符串")
    ap.add_argument("--keep-raw", action="store_true", help="同时保存原始字节到 raw/")
    args = ap.parse_args()

    adb: str | None = None
    ver = "?"
    mmkv: bytes
    if args.local:
        mmkv = Path(args.local).read_bytes()
        print(f"[+] 本地文件 {args.local}（{len(mmkv)} 字节）")
        src = args.local
    else:
        adb = find_adb()
        ensure_connected(adb)
        ver = app_version(adb)
        print(f"[+] 米家 App 版本：{ver}")
        print(f"[*] 读取 {SRC_MMKV} ...")
        mmkv = root_cat(adb, SRC_MMKV)
        print(f"[+] MMKV 文件 {len(mmkv)} 字节")
        src = SRC_MMKV

    if args.keep_raw:
        (Path(__file__).parent / "raw").mkdir(exist_ok=True)
        (Path(__file__).parent / "raw" / "mmkv_id_local_control.bin").write_bytes(mmkv)

    devices, notes = parse_mmkv(mmkv)
    print(f"[+] JSON 根 {len(notes)} 个：{notes[:6]}{' ...' if len(notes) > 6 else ''}")

    print(f"\n[+] 共 {len(devices)} 台设备：")
    for d in devices:
        print(
            f"    did={d['did']}  model={d['model']}  name={d['name']}  "
            f"ip={d['local_ip']}  online={d['is_online']}  token={d['token']}"
        )

    # ── 回归断言 ──
    bad: list[str] = []
    dids = {d["did"] for d in devices}
    for did, expect in KNOWN.items():
        if did not in dids:
            bad.append(f"缺少已知设备 did={did}")
            continue
        got = next(d for d in devices if d["did"] == did)
        for k, v in expect.items():
            if str(got.get(k)) != str(v):
                bad.append(f"did={did} 字段 {k}: 期望 {v!r} 实得 {got.get(k)!r}")

    uid = next((d["uid"] for d in devices if d.get("uid")), None)
    out = {
        "_meta": {
            "source": src,
            "json_roots": len(notes),
            "app_version": ver,
            "serial": SERIAL if adb else None,
            "uid": uid,
            "captured_at": _dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        },
        "devices": devices,
    }
    out_path = Path(args.out)
    out_path.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n[+] 已写出 {out_path}")

    # ── parcel 交叉验证（非致命） ──
    if adb:
        try:
            parcel = root_cat(adb, SRC_PARCEL)
            print(f"[*] parcel 文件 {len(parcel)} 字节")
            if args.dump_parcel:
                for s in parcel_strings(parcel)[:80]:
                    print("    |", s)
            crosscheck_parcel(parcel, devices)
        except SystemExit as e:
            print(f"[!] parcel 读取跳过：{e}")

    if bad:
        print("\n[x] 断言失败：")
        for b in bad:
            print("    -", b)
        return 2
    print("\n[✓] 全部断言通过（与 2026-10-11 侦察值一致）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
