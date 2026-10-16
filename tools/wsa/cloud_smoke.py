#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
小米云 API 冒烟客户端 —— 直连 https://api.io.mi.com/app/*（纯标准库，任意 python 可跑）。

签名方案：ENCRYPT-RC4（mijiaAPI/micloud 同款，已装机可用）：
  nonce        = b64(8 随机字节 + 分钟时间戳大端)          [gen_nonce]
  signed_nonce = b64(sha256(b64d(ssecurity) + b64d(nonce)))
  rc4_hash__   = b64(sha1(METHOD & uri & k=v… & signed_nonce))   ← 明文参数上算
  之后所有参数值用 RC4(signed_nonce, 丢弃前 1024 字节) 加密并 b64
  signature    = 同 sha1 公式（对加密后的参数再算一次）
  最终表单 = {参数…, signature, ssecurity, _nonce}
  响应 = base64 文本 → RC4 解密（同一 signed_nonce）→ 明文 JSON 或 gzip JSON

用法（凭据来自 acquire_qr.py 产出的 credentials.json）：
  python cloud_smoke.py get-list
  python cloud_smoke.py prop-get --did 1234567890 --siid 2 --piid 1 --piid 5 --piid 6
  python cloud_smoke.py prop-set --did 1234567890 --siid 2 --piid 1 --value true
  python cloud_smoke.py probe --did 1234567890                # 遍历探测设备能力
  python cloud_smoke.py raw --uri /miotspec/prop/get --data '{"params":[…]}'
"""
from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import random
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

BASE = "https://api.io.mi.com/app"
DEFAULT_CRED = Path(__file__).parent.parent / "credential" / "credentials.json"

UA_TEMPLATE = (
    "Android-15-11.0.701-Xiaomi-23046RP50C-OS2.0.212.0.VMYCNXM-"
    "{a}-CN-{c}-{b}-SmartHome-MI_APP_STORE-{a}|{d}|{pass_o}-64"
)


# ─────────────────────────── 密码学小件 ───────────────────────────

def gen_nonce() -> str:
    millis = int(round(time.time() * 1000))
    b = (random.getrandbits(64) - 2**63).to_bytes(8, "big", signed=True)
    part2 = int(millis / 60000)
    b += part2.to_bytes((part2.bit_length() + 7) // 8 or 1, "big")
    return base64.b64encode(b).decode()


def rc4_crypt(key: bytes, data: bytes, drop: int = 1024) -> bytes:
    """RC4；丢弃前 drop 字节密钥流（米家协议约定 1024）。"""
    s = list(range(256))
    j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 0xFF
        s[i], s[j] = s[j], s[i]
    i = j = 0
    for _ in range(drop):
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
    out = bytearray()
    for ch in data:
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
        out.append(ch ^ s[(s[i] + s[j]) & 0xFF])
    return bytes(out)


# ─────────────────────────── 客户端 ───────────────────────────

class CloudError(RuntimeError):
    def __init__(self, code, message):
        super().__init__(f"code={code} message={message}")
        self.code = code
        self.message = message


class MiCloud:
    def __init__(self, cred: dict, base: str = BASE, sign_uri_with_app: bool = False, verbose: bool = False):
        for k in ("userId", "ssecurity", "serviceToken"):
            if not cred.get(k):
                raise SystemExit(f"[x] 凭据缺字段 {k}")
        self.userId = str(cred["userId"])
        self.cUserId = str(cred.get("cUserId") or f"{self.userId}")
        self.ssecurity = cred["ssecurity"]
        self.serviceToken = cred["serviceToken"]
        self.deviceId = str(cred.get("deviceId") or "")
        self.ua = cred.get("ua") or self._gen_ua(cred.get("pass_o"))
        self.base = base.rstrip("/")
        self.sign_uri_with_app = sign_uri_with_app
        self.verbose = verbose

    @staticmethod
    def _gen_ua(pass_o: str | None = None) -> str:
        pass_o = pass_o or "".join(random.choices("0123456789abcdef", k=16))
        r = lambda: "".join(random.choices("0123456789ABCDEF", k=40))  # noqa: E731
        return UA_TEMPLATE.format(a=r(), b=r(), c=r(), d=r(), pass_o=pass_o)

    def _signed_nonce(self, nonce: str) -> str:
        m = hashlib.sha256()
        m.update(base64.b64decode(self.ssecurity))
        m.update(base64.b64decode(nonce))
        return base64.b64encode(m.digest()).decode()

    @staticmethod
    def _sign(uri: str, method: str, signed_nonce: str, params: dict) -> str:
        parts = [method.upper(), uri]
        parts += [f"{k}={v}" for k, v in params.items()]
        parts.append(signed_nonce)
        return base64.b64encode(hashlib.sha1("&".join(parts).encode()).digest()).decode()

    def _enc_params(self, uri: str, data_obj, nonce: str, signed_nonce: str) -> dict:
        signed_uri = ("/app" + uri) if self.sign_uri_with_app else uri
        params = {"data": json.dumps(data_obj, separators=(",", ":"))}
        params["rc4_hash__"] = self._sign(signed_uri, "POST", signed_nonce, params)
        key = base64.b64decode(signed_nonce)
        for k in list(params):
            v = params[k]
            params[k] = base64.b64encode(rc4_crypt(key, v.encode())).decode()
        params["signature"] = self._sign(signed_uri, "POST", signed_nonce, params)
        params["ssecurity"] = self.ssecurity
        params["_nonce"] = nonce
        return params

    def request(self, uri: str, data_obj) -> dict:
        nonce = gen_nonce()
        sn = self._signed_nonce(nonce)
        params = self._enc_params(uri, data_obj, nonce, sn)

        body = urllib.parse.urlencode(params).encode()
        req = urllib.request.Request(self.base + uri, data=body, method="POST")
        req.add_header("User-Agent", self.ua)
        req.add_header("Content-Type", "application/x-www-form-urlencoded")
        req.add_header("Accept-Encoding", "identity")
        req.add_header("miot-accept-encoding", "GZIP")
        req.add_header("miot-encrypt-algorithm", "ENCRYPT-RC4")
        req.add_header("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2")
        cookie = (
            f"cUserId={self.cUserId}; serviceToken={self.serviceToken};"
            f" yetAnotherServiceToken={self.serviceToken}; userId={self.userId}; locale=zh_CN;"
        )
        if self.deviceId:
            cookie += f" PassportDeviceId={self.deviceId};"
        req.add_header("Cookie", cookie)

        if self.verbose:
            print(f"[dbg] POST {self.base + uri} （签名 uri={('/app' + uri) if self.sign_uri_with_app else uri}）")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                raw = resp.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", "replace")
            print(f"[!] HTTP {e.code}：{raw[:300]}")

        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            pass
        try:
            dec = rc4_crypt(base64.b64decode(sn), base64.b64decode(raw))
            try:
                txt = dec.decode("utf-8")
            except UnicodeDecodeError:
                txt = gzip.decompress(dec).decode("utf-8")
            return json.loads(txt)
        except Exception as e:  # noqa: BLE001
            raise SystemExit(f"[x] 响应无法解析：{e}\n原始前 200 字节：{raw[:200]!r}")

    # ── 语义封装 ──
    def get_device_list(self) -> list[dict]:
        res = self.request("/home/device_list", {"getVirtualModel": False, "getHuamiDevices": 1})
        if res.get("code"):
            raise CloudError(res["code"], res.get("message", res.get("desc", "")))
        return res["result"]["list"]

    def prop_get(self, did: str, pairs: list[tuple[int, int]]) -> list[dict]:
        params = [{"did": str(did), "siid": s, "piid": p} for s, p in pairs]
        res = self.request("/miotspec/prop/get", {"params": params})
        if res.get("code"):
            raise CloudError(res["code"], res.get("message", res.get("desc", "")))
        return res["result"]

    def prop_set(self, did: str, values: list[dict]) -> list[dict]:
        params = [{"did": str(did), **v} for v in values]
        res = self.request("/miotspec/prop/set", {"params": params})
        if res.get("code"):
            raise CloudError(res["code"], res.get("message", res.get("desc", "")))
        return res["result"]

    def action(self, did: str, siid: int, aiid: int, in_args: list) -> list[dict]:
        params = [{"did": str(did), "siid": siid, "aiid": aiid, "in": in_args}]
        res = self.request("/miotspec/action", {"params": params})
        if res.get("code"):
            raise CloudError(res["code"], res.get("message", res.get("desc", "")))
        return res["result"]


# ─────────────────────────── CLI ───────────────────────────

def parse_value(v: str):
    low = v.lower()
    if low == "true":
        return True
    if low == "false":
        return False
    try:
        return json.loads(v)
    except json.JSONDecodeError:
        return v


def parse_range(s: str) -> list[int]:
    """'1-12' / '2' / '2,3,5' → 列表"""
    out: list[int] = []
    for part in s.split(","):
        part = part.strip()
        if "-" in part:
            a, b = part.split("-", 1)
            out += list(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="小米云 API 冒烟客户端")
    ap.add_argument("--cred", default=str(DEFAULT_CRED), help="credentials.json 路径")
    ap.add_argument("--base", default=BASE)
    ap.add_argument("--sign-uri-with-app", action="store_true", help="若签名报错，试试带 /app 前缀")
    ap.add_argument("-v", "--verbose", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("get-list")

    p = sub.add_parser("prop-get")
    p.add_argument("--did", required=True)
    p.add_argument("--siid", type=int, required=True)
    p.add_argument("--piid", type=int, action="append", required=True)

    p = sub.add_parser("prop-set")
    p.add_argument("--did", required=True)
    p.add_argument("--siid", type=int, required=True)
    p.add_argument("--piid", type=int, required=True)
    p.add_argument("--value", required=True)

    p = sub.add_parser("action")
    p.add_argument("--did", required=True)
    p.add_argument("--siid", type=int, required=True)
    p.add_argument("--aiid", type=int, required=True)
    p.add_argument("--in", dest="in_args", default="[]", help="入参 JSON 数组")

    p = sub.add_parser("probe")
    p.add_argument("--did", required=True)
    p.add_argument("--siids", default="2,3")
    p.add_argument("--piids", default="1-12")
    p.add_argument("--batch", type=int, default=8)

    p = sub.add_parser("raw")
    p.add_argument("--uri", required=True)
    p.add_argument("--data", required=True)

    args = ap.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    cred = json.loads(Path(args.cred).read_text(encoding="utf-8"))
    cloud = MiCloud(cred, base=args.base, sign_uri_with_app=args.sign_uri_with_app, verbose=args.verbose)

    if args.cmd == "get-list":
        devs = cloud.get_device_list()
        print(f"[+] 共 {len(devs)} 台设备：")
        for d in devs:
            tok = d.get("token", "")
            tok_masked = f"{tok[:6]}…{tok[-4:]}" if len(tok) == 32 else f"({len(tok)}位)"
            print(
                f"    did={d.get('did')}  name={d.get('name')}  model={d.get('model')}"
                f"  online={d.get('isOnline')}  ip={d.get('localip')}  token={tok_masked}"
            )
        return 0

    if args.cmd == "prop-get":
        res = cloud.prop_get(args.did, [(args.siid, p) for p in args.piid])
        for r in res:
            print(f"    siid={r.get('siid')} piid={r.get('piid')} code={r.get('code')} value={r.get('value')!r}")
        return 0

    if args.cmd == "prop-set":
        res = cloud.prop_set(args.did, [{"siid": args.siid, "piid": args.piid, "value": parse_value(args.value)}])
        for r in res:
            print(f"    siid={r.get('siid')} piid={r.get('piid')} code={r.get('code')}")
        return 0

    if args.cmd == "action":
        res = cloud.action(args.did, args.siid, args.aiid, json.loads(args.in_args))
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return 0

    if args.cmd == "probe":
        siids = parse_range(args.siids)
        piids = parse_range(args.piids)
        pairs = [(s, p) for s in siids for p in piids]
        found = 0
        for i in range(0, len(pairs), args.batch):
            chunk = pairs[i : i + args.batch]
            try:
                res = cloud.prop_get(args.did, chunk)
            except CloudError as e:
                print(f"[!] 批 {chunk}：{e}")
                continue
            for r in res:
                if r.get("code") == 0:
                    found += 1
                    print(
                        f"    ✓ siid={r.get('siid')} piid={r.get('piid')}"
                        f"  value={r.get('value')!r}  type={type(r.get('value')).__name__}"
                    )
        print(f"[+] probe 完成：{found}/{len(pairs)} 个属性可读")
        return 0

    if args.cmd == "raw":
        res = cloud.request(args.uri, json.loads(args.data))
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return 0

    return 0


if __name__ == "__main__":
    sys.exit(main())
