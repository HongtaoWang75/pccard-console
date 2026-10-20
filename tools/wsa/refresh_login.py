"""用存储的 passToken 静默续期小米云会话（sid=xiaomiio）。

背景：miotspec 接口对会话有独立的「新鲜度」要求，会话放一阵后 device_list 仍可用、
prop/get 却返回 code=3 (auth error)。此脚本用 credentials.json 里的 passToken
重新换一份 serviceToken（并接收轮换后的 passToken/ssecurity）。

流程（与 MiService/micloud 社区实现一致）：
  1. _sign = SHA1("nonce={nonce}&" + SHA1(ssecurity + "&" + nonce))
  2. POST https://account.xiaomi.com/pass/serviceLoginAuth2
     （表单：_json=true, qs, sid=xiaomiio, user=<userId>, _sign, nonce；
        Cookie：passToken / userId / cUserId）
  3. 成功后响应含 location → GET 它（带 cookie）→ 302 的 Location 里取出 serviceToken
  4. 输出合并后的新凭据 JSON（不落盘，由调用方决定）

用法：
  python refresh_login.py [--cred credentials.json] [--out new.json]
"""
import argparse
import hashlib
import http.cookiejar
import json
import re
import time
import urllib.parse
import urllib.request

ACCOUNT = "https://account.xiaomi.com"
UA = ("Android-7.1.1-1.0.0-ONEPLUS A3010-136-XXXXXXXXXXXX "
      "XiaomiAccount_android_MI_CN")


def sha1_hex(s: str) -> str:
    return hashlib.sha1(s.encode("utf-8")).hexdigest()


class _NoFollow(urllib.request.HTTPRedirectHandler):
    """截获 302 的 Location，不自动跟随。"""

    def __init__(self):
        self.location = None

    def http_error_301(self, req, fp, code, msg, headers):
        self.location = headers.get("Location")
        return fp  # 返回响应对象，停止重定向

    http_error_302 = http_error_303 = http_error_307 = http_error_301


def refresh(cred: dict) -> dict:
    user_id = str(cred["userId"])
    pass_token = cred["passToken"]
    ssecurity = cred["ssecurity"]
    c_user_id = cred.get("cUserId", "")

    cookie_jar = http.cookiejar.CookieJar()
    nofollow = _NoFollow()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookie_jar), nofollow)

    # cookie：passToken / userId / cUserId
    for name, value in (("passToken", pass_token), ("userId", user_id), ("cUserId", c_user_id)):
        if value:
            c = http.cookiejar.Cookie(
                version=0, name=name, value=value, port=None, port_specified=False,
                domain="account.xiaomi.com", domain_specified=True, domain_initial_dot=False,
                path="/", path_specified=True, secure=True, expires=None,
                discard=False, comment=None, comment_url=None, rest=None,
            )
            cookie_jar.set_cookie(c)

    nonce = str(100000 + int(time.time() * 1000) % 9000000)
    sign = sha1_hex(f"nonce={nonce}&{sha1_hex(ssecurity + '&' + nonce)}")

    body = urllib.parse.urlencode({
        "_json": "true",
        "qs": "%3Fsid%3Dxiaomiio%26_json%3Dtrue",
        "sid": "xiaomiio",
        "user": user_id,
        "_sign": sign,
        "nonce": nonce,
    }).encode()

    req = urllib.request.Request(
        f"{ACCOUNT}/pass/serviceLoginAuth2?sid=xiaomiio&_json=true&_locale=zh_CN",
        data=body,
        headers={"User-Agent": cred.get("ua") or UA,
                 "Content-Type": "application/x-www-form-urlencoded"},
    )
    with opener.open(req, timeout=30) as resp:
        raw = resp.read().decode("utf-8", "replace")

    raw = raw.split("&&&START&&&", 1)[-1]
    data = json.loads(raw)
    print(f"[1] serviceLoginAuth2: code={data.get('code')} desc={data.get('desc')!r} "
          f"hasLocation={bool(data.get('location'))}")
    if data.get("code") != 0:
        raise SystemExit(f"续期失败：code={data.get('code')} desc={data.get('desc')}")

    new_pass_token = data.get("passToken") or pass_token
    new_ssecurity = data.get("ssecurity") or ssecurity
    location = data.get("location")
    if not location:
        raise SystemExit("续期失败：响应没有 location")

    # 用「新的」passToken/ssecurity 更新 cookie，再 GET location 拿 serviceToken
    for name, value in (("passToken", new_pass_token), ("userId", user_id), ("cUserId", c_user_id)):
        if value:
            c = http.cookiejar.Cookie(
                version=0, name=name, value=value, port=None, port_specified=False,
                domain="account.xiaomi.com", domain_specified=True, domain_initial_dot=False,
                path="/", path_specified=True, secure=True, expires=None,
                discard=False, comment=None, comment_url=None, rest=None,
            )
            cookie_jar.set_cookie(c)

    req2 = urllib.request.Request(location, headers={"User-Agent": cred.get("ua") or UA})
    with opener.open(req2, timeout=30) as resp:
        loc2 = resp.headers.get("Location") or nofollow.location or ""
        final_url = resp.geturl() or loc2
    # serviceToken 在 302 Location（或最终 URL）里
    probe = final_url or ""
    m = re.search(r"serviceToken=([^&]+)", probe)
    if not m and nofollow.location:
        m = re.search(r"serviceToken=([^&]+)", nofollow.location)
    if not m:
        raise SystemExit(f"续期失败：location 链里没有 serviceToken（final={final_url[:120]!r}）")
    service_token = urllib.parse.unquote(m.group(1))
    print(f"[2] 拿到 serviceToken（{len(service_token)} 字符）")

    out = dict(cred)
    out.update({
        "serviceToken": service_token,
        "passToken": new_pass_token,
        "ssecurity": new_ssecurity,
        "refreshedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "source": "refresh",
    })
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cred", default=r"..\credential\credentials.json")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    cred = json.loads(open(args.cred, encoding="utf-8").read())
    new = refresh(cred)
    text = json.dumps(new, ensure_ascii=False, indent=2)
    if args.out:
        open(args.out, "w", encoding="utf-8").write(text)
        print(f"[+] 已写出 {args.out}")
    else:
        print(text)


if __name__ == "__main__":
    main()
