#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""测试 list_sync 列表同步 + item_added 广播（不依赖消息顺序）"""
import hashlib, json, time, uuid, websocket

PASSWORD = "066596"
PORT = 8899

def sha(s): return hashlib.sha256(s.encode()).hexdigest()

def msg(t, d, dev='pytest'):
    return json.dumps({"v":1,"type":t,"id":uuid.uuid4().hex,"ts":int(time.time()),"device":dev,"data":d}, ensure_ascii=False)

ws = websocket.create_connection(f"ws://127.0.0.1:{PORT}/ws", timeout=15)
ws.send(msg("hello", {"auth": sha(PASSWORD), "ts": int(time.time())}))
r = json.loads(ws.recv())
print("[1] hello ->", r["type"])
assert r["type"] == "welcome"

# 2. 请求最近列表
ws.send(msg("list_sync", {}))
r = json.loads(ws.recv())
items = r["data"]["items"]
print(f"[2] list_data -> {len(items)} items")

# 3. 发送文字 → 收 2 条消息（ack + item_added 广播，顺序不定）
ws.send(msg("sync_text", {"content": "广播测试新文字", "source": "test"}))
types_seen = []
for _ in range(2):
    m = json.loads(ws.recv())
    types_seen.append(m["type"])
    if m["type"] == "item_added":
        print(f"    item_added: {m['data']['type']} / {m['data']['name'][:25]}")
    if m["type"] == "ack":
        print(f"    ack: {m['data'].get('status')}")
print(f"[3] 收到消息: {types_seen}")
assert "item_added" in types_seen and "ack" in types_seen, "缺少 item_added 或 ack"
ws.close()
print("\n✅ list_sync + item_added 全部通过")
