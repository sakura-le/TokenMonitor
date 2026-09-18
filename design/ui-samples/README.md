# Token Monitor · UI 样例包（4 套原创方向，全部为可切换皮肤）

> 交付：4 个单文件 HTML（无外链/无 CDN，双击即开），每个文件内含 §1–§7 完整内容：
> 主面板 760×560 高保真、计价配置对话框、卡片右键菜单、托盘菜单（含子菜单展开态）、
> 悬浮球两态、设计令牌表、动效演示。信息架构严格按 `01-original-features.md` §3-D/E/F，
> 视觉 100% 原创，与原项目"霓虹青橙紫玻璃 HUD"在配色/质感/字形/布局骨架/动效理念五个
> 维度全部拉开。所有样例零 `backdrop-filter`、零模糊，可直接对照实施 XAML。
>
> **用户审核结论（第二轮）**：四套方向全部保留为**可切换皮肤**；全局要求**边边角角圆润化**
> （各套圆角令牌见下文，样例令牌表内已同步）；WPF 实施规范见 `design/03-ui-spec.md`。
> 四套共用同一信息架构与组件结构，皮肤 = 令牌差异。

## 共用演示数据（四套一致，便于横向对比）

| 项 | 值 |
|---|---|
| 模型卡 | glm-4.6（8,641,233 · 342 请求 · +1,234 · 漏3）、deepseek-reasoner（4,208,556 · +862）、qwen3-max（1,984,412 · +318）、kimi-k2-0905（736,209 · +97） |
| 命中率环 | glm-4.6 = 67.3%（命中 4,635,324 / 未命中 2,252,231） |
| 费用 | ¥42.1371 / $5.8912；deepseek-reasoner = 套餐不计价（Coding Plan） |
| 双时钟 | UTC/ZULU 14:32:07 · LOCAL+8 22:32:07 |
| 页面可交互 | 卡一 实际/倍率切换（数值 ×1.5）、UTC/LOCAL 口径切换（徽章/描边变色）、折线 hover tooltip、数字滚动与折线描边动画重播 |

---

## Sample 1 — 纸墨印刷 Editorial Ink（浅色）
`sample-1-editorial-ink.html`

**理念（<150 字）**：把用量报表做成"每日对账的印刷品"——暖米纸面、墨色活字、印章红点睛；
细规线与双细线（账本语法）代替阴影分层，衬线大标题配等宽数字，动效如翻页般克制。
安静耐看，适合长时间常驻。

- **配色**：纸面 #F3EDDF / 卡面 #FAF6EA / 墨 #221E15 / 印章红 #B5382A / 靛青 #33567E / 赭石 #B08428 / 苔绿 #5E7A45 / 细规线 #D9CFB6
- **字体**：Georgia / Noto Serif SC / 宋体（标题）；Consolas / Cascadia Mono（数字，等宽表格数字）；微软雅黑（正文）
- **动效**：240ms 一次性减速 cubic-bezier(.22,.75,.25,1)，淡入+8px 上移；无循环闪烁
- **圆角令牌（圆润化后）**：窗体 14 · 对话框 14 · 菜单 12 · 卡/图表/时钟 10 · 控件/输入/徽章 8 · 占比条 pill · 球 50%（"圆角铅字"平衡，规线保持直线）
- **与霓虹玻璃差异**：暖纸浅色 vs 冷黑玻璃；印刷规线 vs 光晕描边；衬线+宋体气质 vs 几何无衬线；"翻页"动效 vs 粒子流
- **WPF 落地**：纯色 Brush + Border 描边（标题栏 3px 双细线用两枚 Border）；纸张纹理在 XAML 中省略或低透明 DrawingBrush；硬投影 = DropShadowEffect(BlurRadius=0, ShadowDepth=5, Direction=135)；环形/折线 = Path(ArcSegment/Polyline)+StrokeDashOffset 动画；印章方标 = Border(圆角)+Viewbox 文字

## Sample 2 — 石墨终端 Graphite Terminal（深色）
`sample-2-graphite-terminal.html`

**理念（<150 字）**：本地仪表/遥测终端——石墨纯平面板、1px 描边、刻度标尺与卡片角标记号，
全等宽字形，唯一强调色磷绿，状态色只作语义。零渐变零辉光零毛玻璃：数据本身就是全部
装饰；动效为步进式刷新 + 状态条光标闪烁。深色基调的"非玻璃"答案。

- **配色**：底 #101418 / 面板 #1A2025 / 凸面 #202830 / 凹面 #131920 / 描边 #3D4852 / 磷绿 #8FCE58 / 钢青 #5E9FC4 / 琥珀 #D9A441 / 信号红 #E0685A / 正文 #D8E0E6
- **字体**：全等宽 Cascadia Mono / Consolas；中文回退微软雅黑；微标签 9px · 0.26em 字距
- **动效**：90–120ms 步进切换 cubic-bezier(.3,0,.2,1)；数字 500ms 步进滚动；唯一循环动效 = 1.1s 光标闪烁（面积仅 6×11px）
- **圆角令牌（圆润化后）**：窗体 12 · 卡片 7 · 菜单 9 · 对话框 10 · 控件/输入/徽章 5–6 · 占比条 3 · 悬浮条 14 · 球 50%；折线/弧线一律圆头线帽（stroke-linecap round），角标记号 L 形改圆角
- **与霓虹玻璃差异**：哑光纯平 vs 发光玻璃；单一磷绿 vs 青橙紫三霓虹；等宽仪表栅格 vs 流体卡片；步进遥测 vs 粒子漂浮
- **WPF 落地**：最易实现——纯 SolidColorBrush + 1px Border；刻度标尺/条内刻纹 = DrawingBrush 平铺；LED = Ellipse；光标闪烁 = DiscreteDoubleKeyFrame opacity；角标记号 = 两枚 L 形圆角 Border

## Sample 3 — 瑞士网格 Swiss Grid（浅色）
`sample-3-swiss-grid.html`

**理念（<150 字）**：International Style 数据海报——粗黑体大数字是唯一主角，瑞士红管
品牌与告警、钴蓝管数据；秩序即美感，卡片错峰滑入一次播完即静止。圆润化后由"发丝缝线
网格"转为**独立大柔角白卡**，网格基因保留在标题栏分格、侧栏规线与直排节奏中。

- **配色**：底灰 #EDEDEA / 面板白 #FFFFFF / 墨黑 #111111 / 瑞士红 #E2231A / 钴蓝 #2440C8 / 信号绿 #1F7A3D / 琥珀 #C77800 / 发丝线 #D6D6D0
- **字体**：Segoe UI Variable / Segoe UI / Helvetica（800 重为主）；数字 800 + `tabular-nums`；微标签 9px · 0.3em 全大写
- **动效**：120ms 快切；卡片 320ms 错峰滑入（50ms 步进）；折线生长后终点红点弹现；常驻无循环动效
- **圆角令牌（圆润化后）**：窗体 16 · 卡/对话框/菜单 14 · 图表 12 · 时钟/费用/按钮 11 · 输入/chips 9 · 徽章 7 · 占比条 pill · 球 50%；发丝线仍为直线但止于圆角边界，勾选符由 ■ 改 ●
- **与霓虹玻璃差异**：白底黑字 vs 黑底彩光；规线网格 vs 玻璃浮岛；巨型序号+红顶杠选中态 vs 霓虹描边选中态；错峰直滑 vs 漂浮光晕
- **WPF 落地**：卡片 = 圆角 Border（CornerRadius 14）独立排布；红顶杠选中 = inset 阴影或上缘 3px Rectangle（圆角随卡）；巨型序号 = 低对比 TextBlock 压底；错峰滑入 = RenderTransform.X Storyboard + BeginTime 阶梯；硬投影 = BlurRadius=0 DropShadowEffect

## Sample 4 — 苍穹空域 Sky HUD（天青浅底）★新增
`sample-4-skyhud.html`

**理念（<150 字）**：战斗机座舱 HUD / 制空权美学：天青云白座舱基调、尾焰橙强调、海军蓝
仪表墨色；雷达扫描环、瞄准框选中态、任务简报排版、ZULU/LOCAL 双钟。全员大圆角与圆头
线帽；"激烈"来自航空语汇与配色对撞，"耐看"来自低刺激动效纪律。

- **配色**：天空底 #DCECFA→#E7F2FC（垂直渐变）/ 云白 #FFFFFF / 海军墨 #10304F / 天青 #1B7BD6 / 尾焰橙 #FF5A1F / 警报红 #E23A26 / 琥珀 #F5A623 / 通行绿 #18A05E / 云线 #D7E7F6 / 次级 #48688C
- **字体**：Bahnschrift（Win10+ 自带 DIN 系座舱体）/ Segoe UI；数字 800 + tabular；数据位 Consolas；中文微软雅黑
- **动效**：180–600ms 平滑减速 cubic-bezier(.25,.7,.3,1)；唯一循环动效 = 雷达扫描扇 6s 匀速（透明度 ≤.30）；数据到达做 1.06→1 缩放脉冲
- **圆角令牌（天生圆润）**：窗体 20 · 卡/侧栏 15–17 · 菜单/对话框 16–20 · 输入 10 · 按钮/徽章/胶囊 999 · 球 50%
- **与霓虹玻璃差异**：天青浅底云白面板 vs 暗黑玻璃；尾焰橙单强调 vs 三色霓虹；航空仪表语汇（雷达/瞄准框/简报）vs 赛博粒子；环境影柔和短距 vs 大范围光晕
- **WPF 落地**：天空底 = 顶部 LinearGradientBrush + 两枚低透明 RadialGradientBrush 椭圆（云）；雷达扫描 = 扇形 Path + RotateTransform 6s Linear（RenderTransform 无布局开销）；瞄准框 = 4 枚 L 形圆角 Border；斜纹警戒条 = DrawingBrush 平铺 135°；胶囊 = Border CornerRadius=Height/2；机徽 = 三层 Ellipse 组合
- **常驻克制（重点）**：扫描扇 Opacity 0.28、周期 ≥5s；无红色告警频闪；漏抓横幅为静态斜纹无动画

---

## 通用 WPF 实施备注（四套皆适用）

1. **禁用项已全部规避**：四套样例均无 backdrop-filter/毛玻璃/外部图像/CDN；所有质感来自纯色、少量渐变（S1 纸纹 1.4%、S4 天空/云）、1–1.5px 描边与矢量绘制，WPF 全部可绘。
2. **图表矢量**：环形图 = `Path`+`ArcSegment`（或 Ellipse+StrokeDashArray）；折线 = `Polyline`/`StreamGeometry`（圆头 PenLineCap.Round）；占比条 = Grid+Rectangle；悬停 tooltip = Adorner/Popup。
3. **数字可读性**：数值全部等宽/表格数字（S1/S2 等宽字体、S3/S4 800 重+tabular；WPF 用 Consolas/Cascadia Mono 或 Bahnschrift+tnum）；千分位 ViewModel `N0` 格式化。
4. **常驻克制**：循环动效全目录 = 跑马灯匀速（hover 暂停）、S2 光标 1.1s 闪烁（6×11px）、S4 雷达扫描 6s（Opacity .28）；无大面积高饱和闪烁。
5. **透明窗口**：`AllowsTransparency=True` + 无边框 Window + 根 Border CornerRadius（各肤窗体圆角令牌）；透明度滑杆绑定 Window.Opacity（S1/S3 下限 60%，S2 70%，S4 65%）。
6. **口径/倍率状态可视化**：双击卡切口径时——S1 描边换赭石、S2 徽章描边换琥珀、S3 徽章换琥珀块、S4 胶囊徽章换琥珀+卡描边琥珀；倍率模式显示 ×N 徽章。
7. **皮肤切换**：四套共用布局与组件结构，皮肤 = ResourceDictionary 令牌差异；运行时热切换方案与控件模板规格见 `design/03-ui-spec.md`（唯一样式依据）。

## 文件清单

```
design/ui-samples/
├─ sample-1-editorial-ink.html      # 纸墨印刷（浅色·印刷排印）
├─ sample-2-graphite-terminal.html  # 石墨终端（深色·仪表遥测）
├─ sample-3-swiss-grid.html         # 瑞士网格（浅色·International Style）
├─ sample-4-skyhud.html             # 苍穹空域（天青·座舱 HUD）★新增
└─ README.md
```
