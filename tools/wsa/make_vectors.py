#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 C# 签名移植的跨语言一致性测试向量（确定性：固定 nonce、合成 ssecurity）。

产出 vectors.json：
  - signed_nonce / rc4_hash__ / data(密文) / signature 的期望值（C# 必须逐字节一致）
  - 两个响应解密样例（明文 JSON 与 gzip JSON 各一）
用法：python make_vectors.py
"""
from __future__ import annotations

import base64
import gzip
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from cloud_smoke import MiCloud  # noqa: E402  复用已被真机验证的实现

HERE = Path(__file__).parent

# ★ 合成密钥与固定 nonce：向量文件不含任何真实凭据，可安全进仓库/给 C# 单测
FAKE_SSECURITY = base64.b64encode(bytes(range(16))).decode()      # AAECAwQFBgcICQoLDA0ODw==
FAKE_NONCE = base64.b64encode(bytes(range(12))).decode()          # AAECAwQFBgcICQoL

CASES = [
    ("/miotspec/prop/get", {"params": [{"did": "1234567890", "siid": 2, "piid": 1}]}),
    ("/miotspec/prop/set", {"params": [{"did": "1234567890", "siid": 2, "piid": 1, "value": True}]}),
    ("/miotspec/prop/set", {"params": [{"did": "1234567890", "siid": 2, "piid": 1, "value": False}]}),
    ("/home/device_list", {"getVirtualModel": False, "getHuamiDevices": 1}),
]

RESPONSE_PLAIN = '{"code":0,"result":[{"did":"1234567890","siid":2,"piid":1,"code":0,"value":true}]}'
RESPONSE_GZIP = '{"code":0,"result":{"list":[{"did":"1234567890","name":"装机卡"}]}}'


def main() -> None:
    cloud = MiCloud({"userId": "1", "ssecurity": FAKE_SSECURITY, "serviceToken": "fake"})
    sn = cloud._signed_nonce(FAKE_NONCE)

    vectors = []
    for uri, data in CASES:
        data_json = json.dumps(data, separators=(",", ":"), ensure_ascii=False)
        form = cloud._enc_params(uri, data, FAKE_NONCE, sn)
        vectors.append({
            "uri": uri,
            "data_json": data_json,
            "signed_nonce": sn,
            "expected": {
                "data": form["data"],
                "rc4_hash__": form["rc4_hash__"],
                "signature": form["signature"],
            },
        })

    # 响应解密样例：Python 侧按同一算法"加密"出响应，C# 侧解密后应得到原文
    key = base64.b64decode(sn)
    from cloud_smoke import rc4_crypt  # noqa: E402

    resp_plain_b64 = base64.b64encode(rc4_crypt(key, RESPONSE_PLAIN.encode())).decode()
    resp_gzip_b64 = base64.b64encode(rc4_crypt(key, gzip.compress(RESPONSE_GZIP.encode()))).decode()

    out = {
        "_meta": {
            "note": "合成 ssecurity/nonce，非真实凭据；由 make_vectors.py 生成",
            "ssecurity": FAKE_SSECURITY,
            "nonce": FAKE_NONCE,
        },
        "sign_vectors": vectors,
        "decrypt_vectors": [
            {"kind": "plain", "body_b64": resp_plain_b64, "expected_json": RESPONSE_PLAIN},
            {"kind": "gzip", "body_b64": resp_gzip_b64, "expected_json": RESPONSE_GZIP},
        ],
    }
    out_path = HERE / "vectors.json"
    out_path.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[+] 已写出 {out_path}（{len(vectors)} 个签名向量 + 2 个解密向量）")
    print(f"    signed_nonce = {sn}")
    print(f"    首条 signature = {vectors[0]['expected']['signature']}")


if __name__ == "__main__":
    main()
