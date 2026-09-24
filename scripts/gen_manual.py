#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成《流转》使用说明书 Word 文档"""
from docx import Document
from docx.shared import Pt, Cm, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH

doc = Document()

# 页面设置 - A4
sec = doc.sections[0]
sec.page_width = Cm(21)
sec.page_height = Cm(29.7)
sec.top_margin = Cm(2)
sec.bottom_margin = Cm(2)
sec.left_margin = Cm(2.5)
sec.right_margin = Cm(2.5)

# 默认字体
style = doc.styles['Normal']
style.font.name = '微软雅黑'
style.font.size = Pt(10.5)
style.element.rPr.rFonts.set(__import__('docx').oxml.ns.qn('w:eastAsia'), '微软雅黑')

ACCENT = RGBColor(0x3B, 0x82, 0xF6)  # 蓝色
DARK = RGBColor(0x1F, 0x29, 0x37)
GRAY = RGBColor(0x66, 0x66, 0x66)

def h1(text):
    p = doc.add_heading(text, level=1)
    for run in p.runs:
        run.font.color.rgb = ACCENT
        run.font.name = '微软雅黑'
    return p

def h2(text):
    p = doc.add_heading(text, level=2)
    for run in p.runs:
        run.font.color.rgb = DARK
        run.font.name = '微软雅黑'
    return p

def para(text, bold=False, color=None, size=10.5):
    p = doc.add_paragraph()
    run = p.add_run(text)
    run.bold = bold
    run.font.size = Pt(size)
    if color: run.font.color.rgb = color
    return p

def bullet(text):
    p = doc.add_paragraph(text, style='List Bullet')
    for run in p.runs:
        run.font.name = '微软雅黑'
    return p

# ============ 封面 ============
title = doc.add_paragraph()
title.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = title.add_run('\n⚡⚡⚡\n\n流 转\n')
run.bold = True
run.font.size = Pt(36)
run.font.color.rgb = ACCENT
run.font.name = '微软雅黑'

sub = doc.add_paragraph()
sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = sub.add_run('跨应用素材暂存中转站 · 简易使用说明书')
run.font.size = Pt(14)
run.font.color.rgb = GRAY

version = doc.add_paragraph()
version.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = version.add_run('\n版本：v1.0   适用人群：0 基础新手')
run.font.size = Pt(10)
run.font.color.rgb = GRAY

doc.add_page_break()

# ============ 目录说明 ============
h1('📋 目录')
for t in ['一、这是什么软件？', '二、如何启动和退出', '三、界面长什么样',
          '四、最常用的 5 个操作', '五、其他实用功能', '六、快捷键大全',
          '七、常见问题 FAQ']:
    bullet(t)

doc.add_page_break()

# ============ 一、这是什么 ============
h1('一、这是什么软件？ 🤔')
para('「流转」是一款帮助你在不同软件之间快速传递文件和文字的桌面小工具。', size=11)
para('简单说：把东西丢进流转，随时拖出来用。')
bullet('✨ 它像个「中转站」：从微信、浏览器、文件夹拖进来的东西，统一存着')
bullet('🎯 不复制源文件：你的原文件永远留在原处，流转只记下它的「地址」')
bullet('💨 即用即拖：需要时把素材拖到任何软件里，就像直接拖文件一样')
bullet('🖼️ 截图即贴：按 Ctrl+V 就能把剪贴板图片存进流转')
bullet('🔒 完全绿色：数据只存在本机，不联网、不上传')

# ============ 二、如何启动退出 ============
h1('二、如何启动和退出 🚀')
h2('启动')
bullet('双击「Liuzhuan.exe」即可启动（绿色版无需安装）')
bullet('启动后屏幕右侧出现一个细长面板，底部通知区域（右下角小箭头）出现 ⚡ 图标')
h2('退出')
bullet('方法一：鼠标移到面板 → 点右上角 ⚙ 设置 → 退出流转')
bullet('方法二：右键通知区域的 ⚡ 图标 → 退出流转')
bullet('⚠️ 注意：点面板右上角 ✕ 只是收起，程序仍在后台运行')

# ============ 三、界面 ============
h1('三、界面长什么样 🖥️')
para('屏幕右侧的长条形窗口就是「流转」主界面：', size=11)
bullet('顶部：标题 + 搜索框（🔍 放大镜图标）')
bullet('分类标签：全部 / 收藏 / 视频 / 文字 / 图片 / 音频 / 其他')
bullet('中间：素材卡片列表（每个卡片显示缩略图或文字摘要）')
bullet('底部：状态栏（素材数量）')
para('面板平时可以「收起」成一条细线（8 像素宽），鼠标放上去自动展开，不挡屏幕。')

# ============ 四、最常用的 5 个操作 ============
h1('四、最常用的 5 个操作 🖱️')
h2('① 存入素材（拖入）')
para('从任何地方（微信聊天、浏览器图片、文件夹）按住鼠标左键，把文件或图片拖到流转面板上松手即可。文字也可以直接拖入！', size=11)
para('小技巧：拖 .txt 文件进来，会自动读取里面的文字内容显示。')

h2('② 存剪贴板内容（Ctrl+V）')
para('先复制或截图（任意软件里 Ctrl+C / 截图工具），然后点一下流转面板（让它获得焦点），按 Ctrl+V，图片或文字就存进去了。', size=11)

h2('③ 取出使用（拖出）')
para('在素材卡片上按住鼠标左键，拖到目标软件（Word、微信、文件夹）里松手，就像拖普通文件一样。', size=11)

h2('④ 单击复制')
para('鼠标单击任意素材卡片，该素材会自动复制到剪贴板。然后在别处直接 Ctrl+V 粘贴即可。', size=11)
bullet('图片/文件 → 复制文件路径，粘贴即引用原文件')
bullet('文字 → 直接复制文字内容')

h2('⑤ 双击查看文字')
para('双击文字类素材，会弹出悬浮窗口，显示完整文字内容。', size=11)
bullet('可以选中其中部分文字')
bullet('点「复制选中」只复制选中的部分')
bullet('点「复制全部」复制全部内容')

# ============ 五、其他功能 ============
h1('五、其他实用功能 ⭐')
h2('收藏 ⭐')
para('鼠标悬停到卡片右下角，点 ☆ 星星按钮收藏；再点一次取消。收藏的素材在「收藏」标签里集中显示，不会被清理。')
h2('搜索 🔍')
para('点顶部放大镜，输入关键词，即可在所有素材中搜索文件名或文字内容。')
h2('删除 🗑️')
para('右键素材 → 删除（或选中后按 Del 键）。删除后按 Ctrl+Z 可撤回！')
h2('悬浮预览 👀')
para('鼠标悬停在图片素材上，会自动弹出原图大图预览，松开鼠标关闭。')
h2('通知区域常驻 ⚡')
para('程序常驻系统通知区域（任务栏右下角），双击 ⚡ 图标可快速显示/隐藏面板。')

# ============ 六、快捷键 ============
h1('六、快捷键大全 ⌨️')
table = doc.add_table(rows=6, cols=2)
table.style = 'Light Grid Accent 1'
data = [
    ('Ctrl + V', '粘贴剪贴板内容到流转（需面板获得焦点）'),
    ('Ctrl + ` (反引号)', '全局热键：随时显示/隐藏面板'),
    ('Del', '删除选中的素材'),
    ('Ctrl + Z', '撤回上一次删除'),
    ('Ctrl + F', '聚焦搜索框'),
]
for i, (k, v) in enumerate(data, start=1):
    row = table.rows[i].cells
    row[0].text = k
    row[1].text = v
for i, cell in enumerate(table.rows[0].cells):
    cell.text = ['快捷键', '作用'][i]

# ============ 七、FAQ ============
h1('七、常见问题 FAQ ❓')
faqs = [
    ('Q：为什么拖文件进不去？',
     'A：确保鼠标拖到面板的蓝色高亮区域再松手；如果面板收起来了，先拖到边缘的细条上，会自动展开。'),
    ('Q：粘贴的图片没有缩略图？',
     'A：先确认剪贴板里确实是图片（可以随便找个聊天框 Ctrl+V 试试）。图片会显示在「图片」标签里。'),
    ('Q：素材存在哪里？',
     'A：数据存在软件所在文件夹的 data 子目录里（data.json 是索引，temp 是剪贴板暂存，thumbnails 是缩略图）。不会存在 C 盘。'),
    ('Q：为什么退出后还能在任务管理器看到进程？',
     'A：关闭窗口只是收起。请用「设置 → 退出流转」或托盘右键菜单退出，这才是彻底退出。'),
    ('Q：换了电脑数据还在吗？',
     'A：把整个软件文件夹拷贝到新电脑即可，数据会自动跟着走（绿色便携版设计）。'),
]
for q, a in faqs:
    para(q, bold=True, color=DARK)
    para(a, color=GRAY)

# ============ 结尾 ============
doc.add_paragraph()
end = doc.add_paragraph()
end.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = end.add_run('⚡ 流转 —— 让素材在指尖流转 ⚡')
run.font.size = Pt(12)
run.font.color.rgb = ACCENT

from pathlib import Path
out = str(Path(__file__).resolve().parents[1] / 'docs' / '流转使用说明书.docx')
import os
os.makedirs(os.path.dirname(out), exist_ok=True)
doc.save(out)
print('DOCX saved:', out)
