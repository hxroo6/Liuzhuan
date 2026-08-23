"""演示素材生成：热区展开 + 剪贴板监控捕获动图"""
import ctypes, time, os
from PIL import ImageGrab

OUT = r"F:/QW《流转》 - 副本/release_package/promo"
os.makedirs(OUT, exist_ok=True)

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

def set_clipboard(text: str):
    CF_UNICODETEXT = 13
    GHND = 0x0042  # GMEM_MOVEABLE | GMEM_ZEROINIT
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    user32.SetClipboardData.restype = ctypes.c_void_p
    user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    for _ in range(10):  # 剪贴板被占用时重试
        if user32.OpenClipboard(0): break
        time.sleep(0.2)
    try:
        user32.EmptyClipboard()
        data = (text + '\0').encode('utf-16-le')
        h = kernel32.GlobalAlloc(GHND, len(data))
        p = kernel32.GlobalLock(h)
        ctypes.memmove(p, data, len(data))
        kernel32.GlobalUnlock(h)
        user32.SetClipboardData(CF_UNICODETEXT, h)
    finally:
        user32.CloseClipboard()

# 1. 鼠标移到屏幕右缘 → 热区展开面板
sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
# 先把鼠标移到屏幕外（避免鼠标在面板里触发收起）
user32.SetCursorPos(10, 10)
time.sleep(0.5)
# 再移到右缘（距右 5px）触发热区展开
user32.SetCursorPos(sw - 5, sh // 2)
time.sleep(0.5)
# 然后移到面板正中央（确保 MouseEnter 完全展开 + 不收起）
user32.SetCursorPos(sw - 170, sh // 2)
time.sleep(1.0)

# 2. 帧1：面板展开（现有素材）
img1 = ImageGrab.grab()
img1.save(os.path.join(OUT, "frame1_full.png"))
panel1 = img1.crop((sw - 480, 0, sw, sh))
panel1.save(os.path.join(OUT, "panel_before.png"))

# 3. 模拟复制 → 剪贴板监控自动收藏
set_clipboard("流转演示：任意 App 复制 → 电脑秒出 ✓ 纯局域网·零云端 #Liuzhuan")
# 截图前再次确保鼠标在面板内
user32.SetCursorPos(sw - 170, sh // 2)
time.sleep(2.5)

# 4. 帧2：新素材置顶出现
img2 = ImageGrab.grab()
img2.save(os.path.join(OUT, "frame2_full.png"))
panel2 = img2.crop((sw - 480, 0, sw, sh))
panel2.save(os.path.join(OUT, "panel_after.png"))

# 5. 合成 GIF（用全屏图，能看到电脑端面板+素材变化）
img1.convert("RGB").save(
    os.path.join(OUT, "liuzhuan_copy_demo.gif"),
    save_all=True, append_images=[img2.convert("RGB")],
    duration=1200, loop=0, optimize=True
)
# 复制 frame2 为"主截图"（panel + 素材列表可见）
import shutil
shutil.copy(os.path.join(OUT, "frame2_full.png"), os.path.join(OUT, "screenshot_main.png"))
print("DONE: GIF + 截图已生成 →", OUT)
