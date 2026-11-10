# -*- coding: utf-8 -*-
"""造三张测试图，验证名片识别（真 OCR 端到端）。
1) card_normal.png     标准微信名片：昵称 + 微信号 + 手机号（号码带空格分隔）
2) card_trap.png       远程码陷阱：11 位数字但在「识别码」语境下，必须被排除
3) card_ambiguous.png  多个号码候选分不清，必须留空
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = r"E:\开发项目\灰Desk重构\assets"
os.makedirs(OUT, exist_ok=True)

FONT = r"C:\Windows\Fonts\msyh.ttc"
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"

def f(size, bold=False):
    path = FONT_BOLD if bold and os.path.exists(FONT_BOLD) else FONT
    return ImageFont.truetype(path, size)

def new_canvas(w=900, h=560):
    img = Image.new("RGB", (w, h), (245, 246, 248))
    d = ImageDraw.Draw(img)
    # 顶部栏
    d.rectangle([0, 0, w, 56], fill=(237, 237, 237))
    d.text((30, 18), "详细资料", font=f(22), fill=(40, 40, 40))
    return img, d

# ---------- 1. 标准名片 ----------
img, d = new_canvas()
d.rectangle([40, 96, 160, 216], fill=(198, 198, 200))  # 头像
d.text((190, 110), "张伟 深圳LED工程", font=f(34, True), fill=(20, 20, 20))
d.text((190, 180), "微信号：wxid_zhangwei2024", font=f(22), fill=(70, 70, 70))
d.text((190, 222), "地区：广东 深圳", font=f(22), fill=(70, 70, 70))
d.text((190, 264), "手机号：138 XXXX 5678", font=f(22), fill=(70, 70, 70))
d.line([40, 320, 860, 320], fill=(225, 225, 228), width=1)
d.text((40, 350), "发送消息", font=f(22), fill=(60, 60, 60))
d.text((40, 400), "语音通话", font=f(22), fill=(60, 60, 60))
img.save(os.path.join(OUT, "card_normal.png"))

# ---------- 2. 远程码陷阱 ----------
img, d = new_canvas()
d.text((60, 130), "本机识别码", font=f(24), fill=(60, 60, 60))
d.text((60, 180), "138XXXX5678", font=f(40, True), fill=(20, 20, 20))
d.text((60, 260), "验证码：a8f3k2", font=f(24), fill=(60, 60, 60))
d.text((60, 330), "向日葵远程控制", font=f(22), fill=(90, 90, 90))
img.save(os.path.join(OUT, "card_trap.png"))

# ---------- 3. 多个号码候选 ----------
img, d = new_canvas()
d.text((60, 120), "李工", font=f(34, True), fill=(20, 20, 20))
d.text((60, 200), "联系人 1380000XXXX", font=f(22), fill=(60, 60, 60))
d.text((60, 250), "另一个 1390000XXXX", font=f(22), fill=(60, 60, 60))
img.save(os.path.join(OUT, "card_ambiguous.png"))

# ---------- 4. 只有昵称，无号码 ----------
img, d = new_canvas()
d.text((60, 120), "王小明", font=f(34, True), fill=(20, 20, 20))
d.text((60, 200), "微信号：wangxm_88", font=f(22), fill=(60, 60, 60))
img.save(os.path.join(OUT, "card_nameonly.png"))

for name in ("card_normal.png", "card_trap.png", "card_ambiguous.png", "card_nameonly.png"):
    p = os.path.join(OUT, name)
    print(name, os.path.getsize(p), "bytes")
