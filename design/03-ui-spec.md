# Token Monitor UI 定稿实施规范（v1.1 · 第二轮圆润化定稿）

> **文件地位**：`src/TokenMonitor.App`（WPF / net10.0-windows）UI 样式的**唯一依据**。
> 信息架构与功能入口以 `01-original-features.md` §3-D/E/F 为准；本文件只回答"长什么样、令牌是什么、XAML 怎么落"。
> 视觉母样：`design/ui-samples/sample-1..4`（四套均为可切换皮肤）。
> 硬约束重申：禁用 backdrop-blur（WPF 无法模糊桌面内容）；数字等宽/表格数字；常驻克制。

---

## 1. 共享信息架构与组件清单（4 皮肤共用，皮肤 = 令牌差异）

### 1.1 三表面总览

| 表面 | 窗体 | 尺寸 | 刷新 |
|---|---|---|---|
| ① 主面板 | 无边框置顶小部件 `MainWindow` | 固定 760×560（`ResizeMode=NoResize` + `ResizeBorderThickness=0`，不可拖边缩放） | StatsSnapshot 200ms |
| ② 托盘 | Hardcodet NotifyIcon + ContextMenu | — | 事件驱动 |
| ③ 悬浮球 | 透明分层窗口 `FloatingBall` | 收起 Ø68 / 展开 470×56 | StatsSnapshot 1s |

### 1.2 主面板布局规格（基准 760×560）

```
┌──────────────────────────────────────────────────────────────┐
│ TitleBar  h46   [徽标][名称][副题/监听地址] … [口径徽章][透明度][置顶][收起到球] │
├──────────────────────────────────────────────────────────────┤
│ MissedBanner  h~34（有漏抓时显示；静态无动画；含"去补录"+关闭）      │
├───────────────────────────────────────┬──────────────────────┤
│ CardGrid  2 列 × N 行（N=显示卡数÷2）     │ DetailSide  w268–272  │
│   gap 10 · 行高均分 · 卡片可隐藏/拖拽排序    │  RingGauge   88–96   │
│   ≥5 卡时保持 2 列滚动；窗口宽 >900 时 3 列  │  Stat4 四行          │
│                                       │  TrendChart7  h~90   │
│                                       │  CostBar  ¥ | $      │
│                                       │  DualClock (UTC|LOCAL)│
└───────────────────────────────────────┴──────────────────────┘
```

- 外边距 12–13，内部统一 8 基准间距阶：`4/8/10/12/13/16/24`。
- 标题栏 46px 即拖拽区（WindowChrome CaptionHeight=46），双击卡片空白区切换 UTC/LOCAL。
- DetailSide 标题随选中卡（默认第一张）；其中 CostBar 有 A/B 两态：A=¥+$ 双格；B=「套餐不计价 · Coding Plan」单格（dashed 描边，金额显示"— —"）。

### 1.3 组件清单

| ID | 组件 | 说明 |
|---|---|---|
| C-01 | TitleBar | 徽标 + 产品名 + 副题（监听地址）+ 口径徽章 + 透明度滑杆(0.6–1.0) + 置顶 Toggle + 收起到球 |
| C-02 | MissedBanner | 「检测到 N 个漏抓请求，请校准补录」+ 去补录 + ✕ |
| C-03 | ModelCard | 提供商徽章 / 模型名 / 漏抓标记 / 请求数 / 实时增量 / 总 Token 大数字 / H·M·O 三比例条 / 实际·倍率分段 / 口径点 / 选中态视觉 / ×N 倍率徽章 |
| C-04 | RatioBar ×3 | H 缓存命中 / M 未命中 / O 输出，占比 of 总量 |
| C-05 | RingGauge | 缓存命中率 = hit÷(hit+miss)；中央 % 数字 |
| C-06 | Stat4 | 命中/未命中/输出/推理 四行，色块 + 千分位 |
| C-07 | TrendChart7 | 近 7 日总 Token 折线（倍率模式画 mul_total），hover tooltip |
| C-08 | CostBar | A 态 ¥/$ 双格（4 位小数）；B 态不计价 |
| C-09 | DualClock | UTC 与 LOCAL 双钟，每秒走字 |
| C-10 | ContextMenu×3 | 卡片 / 模型名 / 标题栏（§1.5） |
| C-11 | TrayMenu | 全项见 §1.5 |
| C-12 | FloatingBall | 两态（§4） |
| C-13 | Dialog ×8 | 设置 / 倍率配置 / 计价配置 / 手动补录 / 操作日志 / 导出 / 时间范围 / 显示隐藏卡片 |
| C-14 | MissChip / MulChip / CaliberBadge | 状态徽章族 |

### 1.4 对话框 ×8（皮肤仅改令牌，结构四肤一致）

0. **设置**：把散在托盘菜单与 config.json/settings.json 里的可配置项收进一个窗口（替代直接编辑配置文件，小白友好）。代理 = 监听地址（只许回环 host:port，与 Core `ValidateListenAddr` 同规则前置校验）+ 供应商行（名称/上游地址/API Key/模型前缀，可增删；MaxTokens/StripParams/默认供应商等高级项不在此编辑、保存时按行原样保留）；通用 = 生效日期基准（UTC/LOCAL 单选）/ 统计时区（-720…840 每 30 分钟共 53 项下拉）/ 悬浮球透明度滑杆（20–100%）与置顶 / 开机自启（注册表 Run，`Infrastructure.AutoStart` 与托盘共用同一实现）。全部随「保存并生效」统一应用：SaveProxy（密钥 DPAPI 加密落盘）→ ReloadProxyConfig（地址变更自动重启监听）→ 差量应用 settings（基准变更走 SetEffectiveDateMode 含 daily 重算；时区走 SetTimezone）→ RefreshChecks。页脚保留「打开配置文件 / 打开导出目录」逃生口。入口：托盘菜单「设置…」、面板标题栏右键菜单。
1. **倍率配置**：模型选择；时段 panel 列表（每 panel = 起止时间下拉 + 该时段倍率，panel 外「+ 添加时段」新增一条）；生效日期；当前/下一版本展示条；Local 口径编辑时展示 UTC↔Local 换算提示。打开即载入该模型链尾版本的时段与倍率。
2. **计价配置**：模型 + 货币(CNY/USD) + 生效日期（按基准日历）；规则卡列表 = 时段行(0.5h，起止时间下拉) + 星期 chips(1–7，周末红字) + 三单价(input/cache/output per 1M) + 删除；版本条「当前 V3 生效中 → 保存覆盖/追加」；底部说明（跨午夜平移 + 自动备份）。打开即载入该模型链尾版本的规则。
   - **模型键**：一律用卡片同源的完整键 `provider/model`。候选列表不列出被同名规范键遮蔽的遗留裸键（如 `deepseek-flash` 之于 `DeepSeek/deepseek-flash`）——它按序排在规范键之前易被误选，保存到无卡片的键上表现为"保存了但不生效"；读取时仍回退到遗留裸键，使旧配置可见并在保存时改写到规范键。保存后若该键没有任何卡片对应，弹出警告明示"界面上不会看到金额变化"。
   - **时段编辑基准**：页脚提示当前按哪个日历填时段（`App.Infrastructure.TimeBasis`）。基准 = settings`effective_date_mode`：`local` → 编辑区按本地时间显示/填写，保存自动 `−offset` 换算为 UTC 存储（含跨午夜拆分与星期平移）；`utc` → 原样。缺该换算会让"按本地时间填的优惠时段"落错窗，金额与厂商后台对不上。
3. **手动补录**：模型选择 / 笔数 N / 命中·未命中·输出 token（total 自动合计只读）/ 提示自动备份与漏抓清理。
4. **操作日志**：范围切换（按模型/全部）+ 列表（时间/动作/模型/详情）。
5. **导出**：范围预设 + 自定义起止 + 模型范围 + 小时明细勾选。
6. **时间范围**：预设（本日/本周/近7日/本月/上月/本季度/本年）+ 自定义。
7. **显示隐藏卡片**：勾选 + 拖拽排序 + 删除选中 + 二次确认。

对话框统一结构：`Header(图标+标题+模型 tag+✕) / 版本或提示条 / Body(表单) / Footer(说明+取消+主按钮)`；遮罩 = 单色半透明纯色（S1 40% 墨、S2 88% 底色、S3 55% 墨、S4 50% 海军）。

### 1.5 右键菜单与托盘菜单

**三层右键：**
- **卡片空白区**：倍率配置… / 计价配置… / ─ / 手动补录… / 操作日志 / 导出数据▸ / 打开配置文件 / ─ / 重置今日…
- **模型名**：刷新数据(F5) / ─ / 数据范围▸（本日✓/本周/近7日/本月/上月/本季度/本年/自定义日期…）
- **标题栏**：显示隐藏卡片… / 操作日志（全部）

**托盘菜单（全项）：**
```
● 代理运行中 · 127.0.0.1:8280        [禁用态, 状态灯]
─
打开面板
设置…
打开配置文件
─
倍率配置…
计价配置…
统计时区 ▸            （UTC−12:00…+14:00，30 分钟步进，当前项勾选）
生效日期基准 ▸        （UTC / Local，单选勾选）
─
导出 ▸               （今日 / 本月 / 近7日 / 今日小时明细 / 打开导出目录）
手动补录…
校准回滚
重载配置
─
开机自启              （Checkable）
悬浮球 ▸             （透明度 40/60/80/100 · 窗口置顶）
导入旧数据…
─
皮肤 ▸               （§6，四项）
退出
```

### 1.6 状态机（视觉映射）

| 状态 | 视觉 |
|---|---|
| 口径 LOCAL（默认） | 徽章：S1 靛青描边 / S2 钢青描边 / S3 钴蓝块前缀 / S4 天青胶囊 |
| 口径 UTC | 徽章换琥珀系（S1 赭石描边 / S2 琥珀描边 / S3 琥珀块 / S4 琥珀胶囊），卡描边同色 |
| 实际值（默认） | 数字 = 原始 token |
| 倍率值 | 数字 = 倍率调整后；卡右上 ×N 徽章（S1 赭石虚线 / S2 绿虚线 / S3 琥珀块 / S4 琥珀胶囊） |
| 选中卡 | S1 墨描边+偏移硬影 / S2 磷绿描边+四角 L 记号 / S3 墨描边+3px 红顶杠+序号染色 / S4 尾焰描边+瞄准框四角 |
| 漏抓 >0 | 横幅显示 + 对应卡 MissChip「漏N」 |

---

## 2. 皮肤机制与令牌字典

### 2.1 DynamicResource 键命名法

所有皮肤差异一律走 `DynamicResource`，键名固定（命名空间前缀 `Tg.` = TokenGrid）：

| 键 | 语义 |
|---|---|
| `Tg.Bg.Window` / `Tg.Bg.Panel` / `Tg.Bg.Card` / `Tg.Bg.Inset` | 窗体/面板/卡/凹面底色 |
| `Tg.Stroke.Strong` / `Tg.Stroke.Hair` | 强描边 / 发丝线 |
| `Tg.Ink.Primary` / `Tg.Ink.Second` / `Tg.Ink.Faint` | 主/次/弱文字 |
| `Tg.Accent.Brand` | 品牌强调（S1 印章红 S2 磷绿 S3 瑞士红 S4 尾焰橙） |
| `Tg.Accent.Soft` | 品牌弱底（横幅底、选中淡底） |
| `Tg.Chart.H` / `Tg.Chart.M` / `Tg.Chart.O` | 三比例条与图表 H/M/O |
| `Tg.State.Danger` / `Tg.State.Warn` / `Tg.State.Ok` / `Tg.State.Info` | 语义色（漏抓/UTC 口径/增量/LOCAL 口径） |
| `Tg.Radius.Window` / `.Card` / `.Menu` / `.Dialog` / `.Control` / `.Badge` / `.Bar` | 圆角阶梯（CornerRadius） |
| `Tg.Shadow.Float` / `Tg.Shadow.Env` | 悬浮物/环境阴影（DropShadowEffect 参数） |
| `Tg.Font.Display` / `Tg.Font.Num` / `Tg.Font.Body` | 字体栈 |
| `Tg.Pct.*` | 尺寸常量（仅布局共享，放 Tokens.Shared） |

**共享布局常量放 `Tokens.Shared.xaml`（StaticResource 可用）**：760/560、46、268、gap 10、间距阶、字号阶、动效时长表（§5）。皮肤字典只含 Brush/Color/CornerRadius/Thickness/Effect/FontFamily。

### 2.2 四套令牌字典

**Skin 1 · EditorialInk（纸墨印刷，浅色）**
```
Bg.Window #F3EDDF  Bg.Panel #FAF6EA  Bg.Card #FAF6EA  Bg.Inset #ECE4D0
Stroke.Strong #221E15  Stroke.Hair #D9CFB6
Ink.Primary #221E15  Ink.Second #5F5847  Ink.Faint #948B77
Accent.Brand #B5382A  Accent.Soft #F2E0D8
Chart.H #33567E  Chart.M #B5382A  Chart.O #B08428
State.Danger #B5382A  Warn #B08428  Ok #5E7A45  Info #33567E
Radius: Window 14 / Card 10 / Menu 12 / Dialog 14 / Control 8 / Badge 8 / Bar 3
Font.Display Georgia+「Noto Serif SC」+SimSun  Font.Num Consolas  Font.Body 微软雅黑
字号: 27/17–21/12.5–13/11–12.5/9.5–10(字距.18em)
Shadow.Float = Blur 0 · Depth 5 · Dir 135 · Opacity .5(墨)   Shadow.Env = 无(规线分层)
动效: 240ms · cubic(.22,.75,.25,1) · 无循环
```

**Skin 2 · GraphiteTerminal（石墨终端，深色）**
```
Bg.Window #101418  Bg.Panel #1A2025  Bg.Card #202830  Bg.Inset #131920
Stroke.Strong #3D4852  Stroke.Hair #2B343C
Ink.Primary #D8E0E6  Ink.Second #8B98A2  Ink.Faint #5A6670
Accent.Brand #8FCE58  Accent.Soft #1E2620
Chart.H #8FCE58  Chart.M #E0685A  Chart.O #5E9FC4
State.Danger #E0685A  Warn #D9A441  Ok #8FCE58  Info #5E9FC4
Radius: Window 12 / Card 7 / Menu 9 / Dialog 10 / Control 5 / Badge 5 / Bar 3（描边一律圆头 PenLineCap.Round）
Font: 全 Cascadia Mono / Consolas（中文回退雅黑）
字号: 26/14–19/12/10.5–11/8–10(字距.2em)
Shadow.Float = Blur 26 · Depth 10 · Dir 270 · #000 Opacity .5   Shadow.Env = 无
动效: 90–120ms · cubic(.3,0,.2,1) · 状态条光标 1.1s Discrete（S2；本地时钟后的光标已移除）
```

**Skin 3 · SwissGrid（瑞士网格，浅色）**
```
Bg.Window #EDEDEA  Bg.Panel #FFFFFF  Bg.Card #FFFFFF  Bg.Inset #F6F6F3
Stroke.Strong #111111  Stroke.Hair #D6D6D0
Ink.Primary #111111  Ink.Second #6E6E6E  Ink.Faint #9C9C96
Accent.Brand #E2231A  Accent.Soft #FBE3E1
Chart.H #2440C8  Chart.M #E2231A  Chart.O #111111
State.Danger #E2231A  Warn #C77800  Ok #1F7A3D  Info #2440C8
Radius: Window 16 / Card 14 / Menu 14 / Dialog 16 / Control 9–11 / Badge 7 / Bar pill
Font: Segoe UI Variable / Segoe UI（800 重）+雅黑
字号: 27/17–20/12–12.5/11–12.5/9–10(字距.3em, 全大写)
Shadow.Float = Blur 0 · Depth 6 · Dir 135 · Opacity .22(墨)   Shadow.Env = 无
动效: 120ms 快切 + 卡片 320ms 错峰 50ms 步进 · cubic(.2,.6,.2,1) · 无循环
```

**Skin 4 · SkyHud（苍穹空域，天青浅底）**
```
Bg.Window = LinearGradient #DCECFA→#E7F2FC(垂直) + 云 RadialGradient 白 .9/.65
Bg.Panel #FFFFFF(.92)  Bg.Card #FFFFFF(.92)  Bg.Inset #F2F8FE
Stroke.Strong rgba(16,48,79,.40)  Stroke.Hair rgba(16,48,79,.16)（1.5px）
Ink.Primary #10304F  Ink.Second #48688C  Ink.Faint #93A9C4
Accent.Brand #FF5A1F  Accent.Soft #FDECE4
Chart.H #1B7BD6  Chart.M #E23A26  Chart.O #10304F
State.Danger #E23A26  Warn #F5A623(UTC 口径文字 #B26A00)  Ok #18A05E  Info #1B7BD6
Radius: Window 20 / Card 15 / Menu 16 / Dialog 20 / Control 10 / Badge 999 / Bar pill
Font.Display Bahnschrift(可加 SemiBold)  Font.Num Bahnschrift(tabular, 回退 Consolas)  Font.Body 雅黑
字号: 27/16–20/12–12.5/11–12.5/9–10(字距.2–.3em)
Shadow.Float = Blur 28 · Depth 12 · Dir 270 · #10304F Opacity .28
Shadow.Env = Blur 10 · Depth 3 · Opacity .10(#1B5A96)
动效: 180–600ms · cubic(.25,.7,.3,1) · 雷达扫描 6s Linear(Op .28)
```

---

## 3. WPF 落地方案

### 3.1 文件结构

```
src/TokenMonitor.App/Themes/
├─ Tokens.Shared.xaml            # 布局常量/字号/间距/动效时长（StaticResource 可用）
├─ Controls.Shared.xaml          # 控件模板骨架（结构，不含皮肤色，全部 DynamicResource 引用）
├─ Skins/
│  ├─ Skin.EditorialInk.xaml     # 皮肤1 令牌字典
│  ├─ Skin.GraphiteTerminal.xaml # 皮肤2
│  ├─ Skin.SwissGrid.xaml        # 皮肤3
│  └─ Skin.SkyHud.xaml           # 皮肤4
└─ Generic.xaml                  # Merged: Tokens.Shared + Controls.Shared + 默认皮肤
```

### 3.2 运行时切换皮肤（不重启）

```csharp
public static void ApplySkin(string key)   // key: "EditorialInk"|"GraphiteTerminal"|"SwissGrid"|"SkyHud"
{
    var md = Application.Current.Resources.MergedDictionaries;
    var old = md.FirstOrDefault(d => d.Source?.OriginalString.Contains("Skins/Skin.") == true);
    var skin = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Skins/Skin.{key}.xaml") };
    md.Add(skin);                    // 先加后删，避免瞬间取不到资源
    if (old != null) md.Remove(old);
    Properties.Settings / ui_state.json 持久化 key;
}
```

约定与坑：
1. **皮肤字典只放 Brush/Color/CornerRadius/Thickness/Effect/FontFamily**，不放 Style/Template → 切换只失效资源引用，不重建控件树，无闪烁。
2. 模板与样式里皮肤相关值**一律 `DynamicResource`**；布局常量用 `StaticResource`（来自 Tokens.Shared，永不变）。
3. **禁止 code-behind 缓存 Brush/Color 实例**（皮肤切换后过期）；需要读值时 `FindResource` 现取。
4. **Storyboard 不能 DynamicResource 到动画目标值**：动画时长/颜色不在皮肤字典中差异化——时长用常量表（§5）；颜色动画若必须随肤，改为绑定属性到 `{DynamicResource}` 的 Brush 上由 WPF 换值，或在切换皮肤时代码重建少量动画。
5. `DropShadowEffect` 定义在皮肤字典（`Tg.Shadow.Float`），模板里 `Effect="{DynamicResource Tg.Shadow.Float}"`；**Effect 不能共享实例**——字典中用 `x:Shared="false"` 的 DropShadowEffect 资源，或每模板实例内联。
6. **透明窗口**：主面板/悬浮球 `AllowsTransparency=True` + `WindowChrome(GlassFrameThickness=0, CaptionHeight=46, ResizeBorderThickness=0, UseAeroCaptionButtons=False)`（主面板 `ResizeMode=NoResize`：尺寸固定，拖边缩放会破坏单列卡片 + 固定 268 侧栏的版式；移动仍由 CaptionHeight 提供）；根 Border CornerRadius=`{DynamicResource Tg.Radius.Window}`，`Background` 绑 `Tg.Bg.Window`。浅色肤(1/3)额外加深描边对比（1px Stroke.Strong），深色肤(2/4)可减弱；窗体外阴影统一由根 Border 的 Effect 提供（见各肤 Shadow.Float）。
7. PerMonitorV2 DPI：`app.manifest` 声明；窗口位置持久化时保存工作区坐标并做越界回调（尺寸固定，不持久化/不还原）。
8. **置顶样式重推**：`AllowsTransparency` 分层窗口在 `Show()` 之后立刻赋 `Topmost` 会与 WS_EX_TOPMOST 的样式应用竞态——表现为"置顶按钮已勾选但窗口不是置顶，手动切换一次才生效"。故 `MainWindow.ApplyTopmost` 在值未变时也强制制造一次属性变更（先翻反再翻回），并在 `Loaded`（`DispatcherPriority.Loaded`）与每次收起到球/恢复后重推；悬浮球在自身 `OnLoaded` 里设置故无此问题。

### 3.3 关键控件样式与 ControlTemplate 规格

**C-03 ModelCard**（Border 圆角 `{Tg.Radius.Card}`，描边 1–1.5px `{Tg.Stroke.Hair}`，背景 `{Tg.Bg.Card}`）：
- 结构 Grid 行：Header(ProviderBadge/Title/MissChip) → Row1(请求数|增量) → Total 大数字 → Bars×3 → Footer(实际/倍率分段+口径点)。
- 点击：卡片任意位置（含空白区与子控件）都选中该卡——模板根 Grid 需 `Background=Transparent`（否则空白区不可命中），窗口侧用 Preview（隧道）事件，且不置 Handled，子控件功能不受影响。
- 口径点文案 = **当前**口径（与详情侧栏 `ScopeTag` 一致）；点击动作写在 ToolTip。
- RatioBar 数值 = 该部分具体 token 数（非百分比）。
- **选中态四肤差异**（SelectorVisual 放卡片模板顶层附加元素）：
  - S1：描边换 `Stroke.Strong` + 偏移硬影；
  - S2：描边换 `Accent.Brand` + 四角 L 形记号（2 枚 10×10 Border，只留相邻两边，圆角 8）；
  - S3：描边换 `Stroke.Strong` + 顶缘 3px 红条（圆角随卡）+ 序号水印染色；
  - S4：描边换 `Accent.Brand` + 四角瞄准框（同 S2 几何、颜色尾焰橙、粗 2px）。
- 口径 UTC：卡描边与徽章换 `State.Warn`（200ms ColorAnimation）。

**C-04 RatioBar**：Grid 4 列 `34 | 150 | Auto | *`（条定宽，右侧数量文本紧随条后，末列 `*` 仅占位；数值 = 该部分具体 token 数，非百分比）；Track 高 6–7，`CornerRadius={Tg.Radius.Bar}`+`ClipToBounds`，底 `Tg.Bg.Inset`（S4 加刻纹 DrawingBrush，S2 加竖刻纹）；Fill = Rectangle `Width` 绑定占比（`*` 轨道内用 `ColumnDefinition` + `WidthConverter`，或 GridSplitter 方式），圆角同 Track。

**C-05 RingGauge**：Viewbox 88–96；底层 track Ellipse(Stroke={Tg.Bg.Inset} 或弱线) + 前景 `Path`(ArcSegment, StrokeThickness 6–9, StrokeStart/EndLineCap=Round, StrokeDashArray 进度) + 中央 % TextBlock；旋转 -90° 起点朝上。S2 外圈叠加虚线刻度圆（DashArray 1 5）；S4 底层为雷达：同心环 2 + 十字线 + 扫描扇 Path(Op .28, RotateTransform 6s Linear 永动)。

**C-07 TrendChart7**：`StreamGeometry/Polyline`（PenLineCap/Join=Round，Stroke 1.6–2.2 `{Tg.Chart.O→Brand}`）+ 3 条弱网格线 + hover 焦点（竖导引虚线 + 焦点圆点）→ Popup tooltip（「MM-DD · N.NM」）。
命中规则：鼠标进入图表即生效（**无需点击**），按**横坐标**取最近的一天（不必移到点上），判定范围 = 整块图表；首次绘制须铺 `Transparent` 填充层，否则空白区不参与命中测试。入场：`StrokeDashOffset` From=L To=0（时长见表）；终点标记（S3/S4）完成后 scale 0→1。

**C-12 MarqueeBall**：见 §4。

**C-02 MissedBanner**：固定高 34 Border（圆角 `{Tg.Radius.Control}`，S1/S2/S3 内嵌浮动条 margin 10–12；S4 斜纹 DrawingBrush 135°）；左侧徽章 + 文案 + 「去补录」+ ✕；显示/收起 = 高度 0↔34 + 淡入 180ms。**静态，无任何循环动画。**

**C-01 TitleBar chrome**：`WindowChrome` 如上；拖拽=系统 Caption；「置顶」=Topmost ToggleButton；「透明度」=Slider 0.6–1.0 绑 `Window.Opacity`（S1/S3 ≥0.6、S2 ≥0.7、S4 ≥0.65，低于下限钳制）；「收起到球」=隐藏面板+显示悬浮球（F2 联动）。

### 3.4 浅色/深色皮肤对窗口描边与阴影的影响

| 皮肤 | 窗体外描边 | 窗体外阴影 |
|---|---|---|
| S1 浅 | 1px #221E15 + 4px 纸色外环 | 硬偏移 14,18 · Blur 0 · 墨 .10 |
| S2 深 | 1px #3D4852 | Blur 26 · Depth 10 · 黑 .5 |
| S3 浅 | 1px #111111 | 硬偏移 8,9 · Blur 0 · 墨 .20 |
| S4 天青 | 1.5px rgba(16,48,79,.4) | Blur 28 · Depth 12 · 海军 .28 |

---

## 4. 悬浮球视觉规格

**收起态（Ø68，边缘吸附，可拖拽）：**
- 分层：底盘（S1 纸白+墨环 / S2 凸面+描边 / S3 纯白+1.5px 墨环 / S4 云白渐变+海军环）/ 命中弧（Stroke 4，起点朝上，`= hit/(hit+miss)`）/ 中心两行：总量缩写（14–15px · 800 · tabular，如 `8.6M`）与增量（9px · `{Tg.State.Ok}`，`▲+1.2K`）。
- S4 专属：球内叠加雷达扫描扇（Op .22，6s）。
- 拖拽中（按住）：**盘内元素**（命中弧 + 雷达 + 数字）scale 1.06 且命中弧描边 4→5.5——底盘 Ø68 不参与缩放（底盘正好铺满 68px 窗口，整盘放大会被窗口裁掉一圈边）；接近屏幕边缘 12px 磁吸，吸附后 180ms 回弹落位。
- 右键 = 托盘菜单；双击 = 回主面板。

**展开态（470×56 胶囊，CornerRadius=高/2 或 14–16）：**
- 结构：`[状态徽 LIVE/LED] | 跑马灯窗口(两端 5% 渐隐遮罩) | 右侧双钟(LOCAL 大字 + UTC 小字)`。
- 条目排版：`模型名(Info色·800) 指标名(Ink.Faint) 值(Ink.Primary·tabular) ▲增量(Ok) / 分隔`，条目水平 padding 16–17。
- 条目内容（两种模式，用户要求）：① **有模型正在跑**（代理在途请求，或 15s 内有过请求）→ 循环该模型的 TOTAL / 命中 / 未命中 / 输出 / 命中率 / 调用次数；② **空闲** → 按面板卡片顺序循环每张可见卡片的 TOTAL 与命中率（取该卡自己的口径与倍率视图）。
- 动画：整条内容复制两份，`TranslateTransform.X` 0→-50% 16s Linear 永动；数据 1s 刷新只改文本**不重置动画**；鼠标悬停 `Pause`。
- 触发：双击球/收起按钮切换；高度 0→56 + 淡入 220ms。

---

## 5. 动效规格表

| 触发器 | 对象 | 属性 | 时长 | 缓动 | 备注 |
|---|---|---|---|---|---|
| 面板显示/隐藏 | MainWindow | Opacity+TranslateY 8px | 240 | cubic(.22,.75,.25,1) | 四肤同 |
| 卡片入场 | ModelCard | Opacity+X(-10→0) | 320 | §2 各肤 | BeginTime 阶梯 50ms×n（S3 为主要表达；其余肤可只淡入） |
| 选中卡切换 | SelectorVisual | 颜色/显隐 | 120–180 | Decelerate | 无回弹 |
| 口径切换 | 徽章/描边 | Color | 200 | Linear | ColorAnimation |
| usage 事件到达 | 增量/总量 | 数字 CountUp | 500–600 | Power3 出 | 到位即静止；S4 追加 1.06→1 脉冲 |
| 悬停焦点 | 折线导引线+圆点+Popup | 显隐 | 0/120 | — | 按横坐标取最近一天；跟随鼠标钳位 |
| 图表入场 | TrendChart7 / RingGauge | StrokeDashOffset | 600–700 | Power3 出 | 仅首次与重算后 |
| 漏抓横幅 | Banner | Height+Opacity | 180 | Decelerate | 静态常驻无闪烁 |
| 球两态切换 | FloatingBall | Size+Opacity | 220 | Decelerate | — |
| 跑马灯 | 展开态 | TranslateX 0→-50% | 16s | Linear 循环 | hover Pause |
| 雷达扫描（S4） | 扫描扇 | Rotate 360° | 6s | Linear 循环 | Op .28 |
| 光标闪烁（S2） | 状态条光标 | Opacity | 1.1s | Discrete 50% | 面积 6×11 |
| 流量激增 | 环境动效 | 见下 | — | — | — |

**「流量激增 → 环境动效加速」映射与克制规则：**
- 触发：60s 滑窗增量 > 基线均值 + 3σ（或 > 50K tokens/s）；解除需连续 30s 回落（滞回）。
- 映射：S4 雷达扫描 6s→2.5s；S2 状态条光标闪烁 1.1s→0.45s；S1/S3 无循环动效 → 改为增量数字的脉冲频率提高（静止→1.2Hz 内一次性脉冲）；跑马灯速度不随激增变化（可读性优先）。
- 克制红线：任何循环动效透明度 ≤.35、频率 ≤2.5Hz、面积 ≤1/8 卡片；激增态只允许同时加速**一处**环境动效；面板永不整体闪烁、永不发声。

---

## 6. 托盘「皮肤」子菜单（4 项命名与缩略描述）

| 菜单项 | 缩略描述（托盘 tooltip/菜单副文本） |
|---|---|
| **纸墨印刷** | 浅色 · 米纸账本：衬线标题 + 印章红 + 细规线 |
| **石墨终端** | 深色 · 纯平仪表：等宽字形 + 磷绿 + 刻度标尺 |
| **瑞士网格** | 浅色 · 数据海报：粗黑大数字 + 红蓝双色 + 柔角白卡 |
| **苍穹空域** | 天青 · 座舱 HUD：雷达扫描 + 尾焰橙 + 瞄准框 |

- 四项为 RadioItem（当前肤勾选）；切换即调 `ApplySkin` 并持久化 `ui_state.json: skin`。
- 命名与样例文件对应：EditorialInk / GraphiteTerminal / SwissGrid / SkyHud。

---

## 7. 验收清单（红线复述）

1. 全部容器/卡片/按钮/输入/菜单/进度条/徽章/图表端点**圆润化**，圆角取值不得偏离 §2.2 阶梯。
2. 全程无 backdrop-blur；悬浮物阴影参数按 §2.2/§3.4；数字等宽/表格。
3. 信息架构、7 对话框、3 层右键、托盘全项、球两态与本规范 §1 一致；皮肤切换不重启、不重建控件树。
4. 常驻动效仅 §5 白名单内循环项；激增映射遵守 §5 克制红线。
