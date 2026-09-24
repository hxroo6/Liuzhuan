#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""测试 get_item（文字全文/文件下载URL）+ HTTP 文件下载"""
import hashlib, json, time, uuid, urllib.request, websocket

import os
PASSWORD = os.environ["LIUZHUAN_TEST_PASSWORD"]  # set locally; never commit pairing credentials
PORT = 8899

def sha(s): return hashlib.sha256(s.encode()).hexdigest()

def msg(t, d, dev='pytest'):
    return json.dumps({"v":1,"type":t,"id":uuid.uuid4().hex,"ts":int(time.time()),"device":dev,"data":d}, ensure_ascii=False)

ws = websocket.create_connection(f"ws://127.0.0.1:{PORT}/ws", timeout=15)
ws.send(msg("hello", {"auth": sha(PASSWORD), "ts": int(time.time())}))
print("[1] hello ->", json.loads(ws.recv())["type"])

# 拉列表
ws.send(msg("list_sync", {}))
items = json.loads(ws.recv())["data"]["items"]
print(f"[2] 列表 {len(items)} 条")

text_id = next((it["id"] for it in items if it["type"] == "Text"), None)
file_id = next((it["id"] for it in items if it["type"] != "Text"), None)
print(f"[3] text_id={text_id[:8] if text_id else None} file_id={file_id[:8] if file_id else None}")

# get_item 文字
if text_id:
    ws.send(msg("get_item", {"id": text_id}))
    r = json.loads(ws.recv())
    content = r["data"].get("content", "")
    print(f"[4] get_item 文字 -> 内容 {len(content)} 字: {content[:30]}...")
    assert len(content) > 0, "文字内容为空"

# get_item 文件 → 下载 URL
if file_id:
    ws.send(msg("get_item", {"id": file_id}))
    r = json.loads(ws.recv())
    url = r["data"].get("downloadUrl", "")
    print(f"[5] get_item 文件 -> {url}")
    assert url, "无下载URL"
    # HTTP 下载测试（前 200 字节）
    req = urllib.request.Request(url, headers={"Range": "bytes=0-199"})
    resp = urllib.request.urlopen(req, timeout=10)
    data = resp.read()
    print(f"[6] HTTP 下载 -> 状态 {resp.status}, 读取 {len(data)} 字节")
    assert resp.status == 200 and len(data) > 0

ws.close()
print("\n✅ get_item + HTTP 下载全部通过")
