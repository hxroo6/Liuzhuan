#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""M1 端到端测试：模拟安卓端连接流转 LanServer 并发送文字"""
import hashlib
import json
import time
import websocket

PASSWORD = "9Y6VFCBM"
PORT = 8899

def sha256hex(s):
    return hashlib.sha256(s.encode("utf-8")).hexdigest()

def make_msg(type_, data=None, device="Pixel 8 测试"):
    return json.dumps({
        "v": 1,
        "type": type_,
        "id": __import__("uuid").uuid4().hex,
        "ts": int(time.time()),
        "device": device,
        "data": data or {}
    }, ensure_ascii=False)

ws = websocket.create_connection(f"ws://127.0.0.1:{PORT}/ws", timeout=10)
print("[1] 已连接 WS")

# 1. 握手
ws.send(make_msg("hello", {"auth": sha256hex(PASSWORD), "ts": int(time.time())}))
welcome = json.loads(ws.recv())
print(f"[2] 握手结果: {welcome['type']}")
assert welcome["type"] == "welcome", f"握手失败: {welcome}"

# 2. 心跳
ws.send(make_msg("heartbeat"))
hb = json.loads(ws.recv())
print(f"[3] 心跳回执: {hb['type']}")

# 3. 发送文字素材（sync_text）
ws.send(make_msg("sync_text", {"content": "M1 端到端测试：来自安卓模拟端的文字", "source": "clipboard_test"}))
ack = json.loads(ws.recv())
print(f"[4] sync_text 回执: {ack['type']} status={ack['data'].get('status')}")

# 4. 发送剪贴板推送（clipboard_push）
ws.send(make_msg("clipboard_push", {"content": "剪贴板测试：局域网联动成功 ✅", "app": "com.test.app"}))
ack2 = json.loads(ws.recv())
print(f"[5] clipboard_push 回执: {ack2['type']} status={ack2['data'].get('status')}")

# 5. 错误口令测试
try:
    ws2 = websocket.create_connection(f"ws://127.0.0.1:{PORT}/ws", timeout=10)
    ws2.send(make_msg("hello", {"auth": sha256hex("wrongpassword"), "ts": int(time.time())}))
    resp = json.loads(ws2.recv())
    print(f"[6] 错误口令响应: {resp['type']} reason={resp['data'].get('reason')}")
    ws2.close()
except Exception as ex:
    print(f"[6] 错误口令连接被拒: {ex}")

ws.close()
print("\n✅ M1 端到端测试完成")
