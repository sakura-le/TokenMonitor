# Token Monitor · UI 样例包（3 套原创方向）

> 交付：3 个单文件 HTML（无外链/无 CDN，双击即开），每个文件内含 §1–§7 完整内容：
> 主面板 760×560 高保真、计价配置对话框、卡片右键菜单、托盘菜单（含子菜单展开态）、
> 悬浮球两态、设计令牌表、动效演示。信息架构严格按 `01-original-features.md` §3-D/E/F，
> 视觉 100% 原创，与原项目"霓虹青橙紫玻璃 HUD"在配色/质感/字形/布局骨架/动效理念五个
> 维度全部拉开。所有样例零 `backdrop-filter`、零模糊，可直接对照实施 XAML。

## 共用演示数据（三套一致，便于横向对比）

| 项 | 值 |
|---|---|
| 模型卡 | glm-4.6（8,641,233 · 342 请求 · +1,234 · 漏3）、deepseek-reasoner（4,208,556 · +862）、qwen3-max（1,984,412 · +318）、kimi-k2-0905（736,209 · +97） |
| 命中率环 | glm-4.6 = 67.3%（命中 4,635,324 / 未命中 2,252,231） |
| 费用 | ¥42.1371 / $5.8912；deepseek-reasoner = 套餐不计价（Coding Plan） |
| 双时钟 | UTC 14:32:07 · LOCAL+8 22:32:07 |
| 页面可交互 | 卡一 实际/倍率切换（数值 ×1.5）、UTC/LOCAL 口径切换（徽章/描边变色）、折线 hover tooltip、数字滚动与折线描边动画重播 |

---

## Sample 1 — 纸墨印刷 Editorial Ink
`sample-1-editorial-ink.html`

**理念（<150 字）**：把用量报表做成"每日对账的印刷品"——暖米纸面、墨色活字、印章红点睛；
细规线与双细线（账本语法）代替阴影分层，衬线大标题配等宽数字，动效如翻页般克制。
浅色方向，安静耐看，适合长时间常驻。

- **配色**：纸面 #F3EDDF / 卡面 #FAF6EA / 墨 #221E15 / 印章红 #B5382A / 靛青 #33567E / 赭石 #B08428 / 苔绿 #5E7A45 / 细规线 #D9CFB6
- **字体**：Georgia / Noto Serif SC / 宋体（标题）；Consolas / Cascadia Mono（数字，等宽表格数字）；微软雅黑（正文）
- **动效**：240ms 一次性减速 cubic-bezier(.22,.75,.25,1)，淡入+8px 上移；无循环闪烁
- **与霓虹玻璃差异**：暖纸浅色 vs 冷黑玻璃；印刷规线 vs 光晕描边；衬线+宋体气质 vs 几何无衬线；直角近方 vs 大圆角浮岛；"翻页"动效 vs 粒子流
- **WPF 落地**：全部纯色 Brush + Border 描边（1px #D9CFB6，标题栏 BorderThickness 0,0,0,3 双线可用两枚 Border 或 DashArray 模拟）；纸张纹理在 XAML 中省略或用低透明 DrawingBrush；硬投影 = DropShadowEffect(BlurRadius=0, ShadowDepth=5, Direction=135)；环形/折线 = Path(ArcSegment/Polyline)+StrokeDashOffset 动画；印章方标 = Border+Viewbox 文字

## Sample 2 — 石墨终端 Graphite Terminal
`sample-2-graphite-terminal.html`

**理念（<150 字）**：本地仪表/遥测终端——石墨纯平色块、1px 描边、顶部刻度标尺与卡片角
标记号，全等宽字形，唯一强调色磷绿，状态色只作语义。零渐变零辉光零毛玻璃：数据本身
就是全部装饰；动效为步进式刷新 + 状态条光标闪烁。深色基调的"非玻璃"答案。

- **配色**：底 #101418 / 面板 #1A2025 / 凸面 #202830 / 凹面 #131920 / 描边 #3D4852 / 磷绿 #8FCE58 / 钢青 #5E9FC4 / 琥珀 #D9A441 / 信号红 #E0685A / 正文 #D8E0E6
- **字体**：全等宽 Cascadia Mono / Consolas；中文回退微软雅黑；微标签 9px · 0.26em 字距
- **动效**：90–120ms 步进切换 cubic-bezier(.3,0,.2,1)；数字 500ms 步进滚动；唯一循环动效 = 1.1s 光标闪烁（面积仅 6×11px）
- **与霓虹玻璃差异**：哑光纯平 vs 发光玻璃；单一磷绿 vs 青橙紫三霓虹；等宽仪表栅格 vs 流体卡片；步进遥测 vs 粒子漂浮；直角 0 圆角 vs 大圆角
- **WPF 落地**：最易实现的一套——纯 SolidColorBrush + 1px Border；刻度标尺 = 叠加两条 Line 的 Canvas 或 DrawingBrush 平铺；条内刻纹 = DrawingBrush 平铺斜纹/竖纹；LED = Ellipse；状态条光标闪烁 = Storyboard opacity 0↔1 steps 用 DiscreteDoubleKeyFrame；角标记号 = 两枚 L 形 Border 叠放

## Sample 3 — 瑞士网格 Swiss Grid
`sample-3-swiss-grid.html`

**理念（<150 字）**：International Style 数据海报——白色模数网格被 1px 发丝线切开，
粗黑体大数字是唯一主角；瑞士红管品牌与告警、钴蓝管数据。全直角、零阴影、零渐变、
零纹样，秩序即美感；卡片错峰滑入一次播完即静止。

- **配色**：底灰 #EDEDEA / 面板白 #FFFFFF / 墨黑 #111111 / 瑞士红 #E2231A / 钴蓝 #2440C8 / 信号绿 #1F7A3D / 琥珀 #C77800 / 发丝线 #D6D6D0
- **字体**：Segoe UI Variable / Segoe UI / Helvetica（800 重为主）；数字 800 + `tabular-nums`；微标签 9px · 0.3em 全大写
- **动效**：120ms 快切；卡片 320ms 错峰滑入（50ms 步进）；折线生长后终点红点弹现；常驻无循环动效
- **与霓虹玻璃差异**：白底黑字 vs 黑底彩光；发丝线网格单元 vs 玻璃浮岛卡片；巨型序号+红顶杠选中态 vs 霓虹描边选中态；错峰直滑 vs 漂浮光晕
- **WPF 落地**：卡片网格 = UniformGrid/Grid + 共享 1px 分隔（每卡 Border Margin 0.5 或背景缝线法）；红顶杠选中 = Border 上缘 3px BorderThickness 或内嵌 Rectangle；巨型序号 = TextBlock 低对比前景色压底；数字对齐：Segoe UI 数字用 Typography 或大数字改用 Consolas；错峰滑入 = 每卡 RenderTransform.X Storyboard + BeginTime 阶梯；硬投影 = BlurRadius=0 DropShadowEffect

---

## 通用 WPF 实施备注（三套皆适用）

1. **禁用项已全部规避**：三套样例均无 backdrop-filter/毛玻璃/外部图像/CDN；样例中的所有质感都来自纯色、渐变（仅 S1 纸面 3% 透明度细纹，可选）、1px 描边与矢量绘制，WPF 全部可绘。
2. **图表矢量**：环形图 = `Path` + `ArcSegment`（或 Ellipse+StrokeDashArray）；折线 = `Polyline`/`StreamGeometry`；占比条 = Grid+Rectangle 宽度绑定；悬停 tooltip = Adorner 或 Popup。
3. **数字可读性**：三套的数值均使用等宽/表格数字（S1/S2 直接等宽字体；S3 用 800 重+tabular），XAML 中对应 `FontFamily=Consolas/Cascadia Mono` 或 `Typography`；千分位由 ViewModel 格式化 `N0`。
4. **常驻克制**：三套的常驻循环动效只有三种且全部低面积——球体命中弧静态、跑马灯匀速滚动（hover 暂停）、S2 光标 1.1s 闪烁；无大面积高饱和闪烁，符合"常驻不刺眼"。
5. **透明窗口**：面板圆角/外留白由 `AllowsTransparency=True` + 无边框 Window + 根 Border CornerRadius 实现；窗口透明度 = `Window.Opacity` 由标题栏滑杆/托盘绑定（S1/S3 建议下限 60%，S2 建议下限 70%，低于此深浅底都会伤数字可读性）。
6. **口径/倍率状态可视化**：双击卡片切口径时，S1 换描边色（赭石）、S2 换徽章描边（琥珀）、S3 换徽章色块（琥珀）——三种方案都在令牌表内，实施时任选其一并保持全卡一致。

## 文件清单

```
design/ui-samples/
├─ sample-1-editorial-ink.html      # 纸墨印刷（浅色·印刷排印）
├─ sample-2-graphite-terminal.html  # 石墨终端（深色·仪表遥测）
├─ sample-3-swiss-grid.html         # 瑞士网格（浅色·International Style）
└─ README.md
```
