#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""M4 多设备端到端测试：3 台设备并发连接（全量消费消息，无残留）"""
import hashlib, json, time, uuid, websocket

PASSWORD = "066596"
PORT = 8899

def sha(s): return hashlib.sha256(s.encode()).hexdigest()

def msg(t, d, dev):
    return json.dumps({"v":1,"type":t,"id":uuid.uuid4().hex,"ts":int(time.time()),"device":dev,"data":d}, ensure_ascii=False)

class Device:
    def __init__(self, name):
        self.name = name
        self.ws = websocket.create_connection(f"ws://127.0.0.1:{PORT}/ws", timeout=15)
        self.ws.send(msg("hello", {"auth": sha(PASSWORD), "ts": int(time.time())}, name))
        assert json.loads(self.ws.recv())["type"] == "welcome"
        print(f"[✓] {self.name} 连接成功")

    def recv_n(self, n, timeout=15):
        """收取 n 条消息（顺序不定）"""
        self.ws.settimeout(timeout)
        out = []
        for _ in range(n):
            out.append(json.loads(self.ws.recv()))
        return out

    def close(self):
        try: self.ws.close()
        except: pass

print("=== 1. 三台设备并发连接 ===")
d1, d2, d3 = Device("Pixel 8 测试"), Device("Redmi K70"), Device("iPad 平板")

print("\n=== 2. A 发文字 → A 收 ack+广播；B/C 收广播 ===")
d1.ws.send(msg("sync_text", {"content": "多设备广播测试", "source": "test"}, d1.name))
a1 = d1.recv_n(2)   # ack + 自己的广播
assert any(m["type"] == "ack" for m in a1)
assert any(m["type"] == "item_added" and m["data"]["name"] == "多设备广播测试" for m in a1)
b2 = d2.recv_n(1)[0]; c2 = d3.recv_n(1)[0]
assert b2["type"] == "item_added" and c2["type"] == "item_added"
print("[✓] A 收 ack+自身广播；B、C 收广播")

print("\n=== 3. B 断开 → A/C 不受影响 ===")
d2.close()
time.sleep(1)
d1.ws.send(msg("sync_text", {"content": "B断开后的测试", "source": "test"}, d1.name))
a3 = d1.recv_n(2)
c3 = d3.recv_n(1)[0]
assert c3["type"] == "item_added" and c3["data"]["name"] == "B断开后的测试"
print("[✓] B 断开后 A/C 通信正常")

print("\n=== 4. C 发文字 → A 收广播 ===")
d3.ws.send(msg("sync_text", {"content": "来自平板的文字", "source": "test"}, d3.name))
c4 = d3.recv_n(2)
a4 = d1.recv_n(1)[0]
assert a4["type"] == "item_added" and a4["data"]["name"] == "来自平板的文字"
print("[✓] A 收到 C 的广播")

for d in (d1, d3): d.close()
print("\n✅ M4 多设备测试全部通过")
