#!/usr/bin/env python3
"""把 icon-<尺寸>.png 合成为 Windows 图标（.ico）。

用法：python3 tools/make-ico.py <PNG 所在目录> <输出的 .ico>
PNG 由 tests/LightTextEditor.App.Tests/IconExport.cs 渲染（设置环境变量 LTE_ICON_DIR 后运行测试）。

48 像素及以下存为 32 位 BMP（各种程序都认），更大的存为 PNG（Windows Vista 起支持，文件小得多）。
需要 Pillow（pip install pillow）。
"""
import io
import re
import struct
import sys
from pathlib import Path

from PIL import Image


def bmp_entry(image: Image.Image) -> bytes:
    """ICO 里的 BMP：BITMAPINFOHEADER（高度为两倍）+ 自下而上的 BGRA 像素 + 全零 AND 掩码。"""
    w, h = image.size
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes('raw', 'BGRA')
    mask_row = ((w + 31) // 32) * 4
    return header + pixels + bytes(mask_row * h)


def main(source: str, output: str) -> None:
    images = {}
    for path in Path(source).glob('icon-*.png'):
        m = re.fullmatch(r'icon-(\d+)\.png', path.name)
        if m:
            images[int(m.group(1))] = Image.open(path).convert('RGBA')
    sizes = sorted(images)
    if not sizes:
        sys.exit(f'{source} 里没有 icon-<尺寸>.png')

    entries = []
    for size in sizes:
        image = images[size]
        if size <= 48:
            data = bmp_entry(image)
        else:
            buffer = io.BytesIO()
            image.save(buffer, 'PNG', optimize=True)
            data = buffer.getvalue()
        entries.append((size, data))

    offset = 6 + 16 * len(entries)
    out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    Path(output).write_bytes(out)
    print(f'{output}: {", ".join(str(s) for s in sizes)}（{len(out):,} 字节）')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
