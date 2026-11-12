# UEToVS 品牌图标

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| `UEToVS.svg` | 矢量（16 px 网格） | 源文件 |
| `UEToVS16.png` | 16×16 | VSCT 位图 `guidUEToVSImages:bmpLogo`：工具栏最左侧按钮、菜单 “设置…” |
| `UEToVS32.png` | 32×32 | 高 DPI 备用 |
| `Icon.png` | 128×128 | VSIX `Icon` |
| `PreviewImage.png` | 256×256 | VSIX `PreviewImage` |

图形：靛蓝→蓝色渐变（`#6B4FE8` → `#1F7AE0`）圆角方块，白色 “U” 的右侧竖笔以向上的箭头收尾（“跳转”）。
WPF 中的同款图形是 `src/UEToVS.UI/Atoms/BrandMark.cs`（设置窗口、性能面板、组件画廊的标题栏），改图形时两处一起改。

PNG 由 SVG 的同一几何用 Pillow 超采样渲染导出（无需 Inkscape）；也可以用任意 SVG 工具按上表尺寸导出。
