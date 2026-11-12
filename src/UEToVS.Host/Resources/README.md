# UEToVS 品牌图标

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| `UEToVS.svg` | 矢量（16 px 网格） | 源文件 |
| `UEToVS16.png` | 16×16 | VSCT 位图 `guidUEToVSImages:bmpLogo`：工具栏最左侧按钮、菜单 “设置…” |
| `UEToVS32.png` | 32×32 | 高 DPI 备用 |
| `Icon.png` | 128×128 | VSIX `Icon` |
| `PreviewImage.png` | 256×256 | VSIX `PreviewImage` |

图形（v0.3.1 起）：靛蓝→蓝色渐变（`#6B4FE8` → `#1F7AE0`）圆角方块上的白色游戏手柄——圆角机身带两个握把，
左侧十字键、右侧四个功能键（都是镂空）。几何按 16 px 网格对齐像素，16×16 时边缘清晰；16–20 px 时功能键画成整像素方块，
32 px 起画成圆点。

WPF 中的同款图形是 `src/UEToVS.UI/Atoms/BrandMark.cs`（设置窗口、性能面板、组件画廊的标题栏），改图形时两处一起改。

SVG 和全部 PNG 由 `scripts/render_brand_icons.py` 生成（只需要 Pillow，超采样后按面积平均缩小）：

```
python3 scripts/render_brand_icons.py
```
