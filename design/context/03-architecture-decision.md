# 架构决策书（已批准 v1.1）

## 1. 选型结论

**C# / .NET 10 WPF 单进程单 exe**（用户提议并批准）。
本机核验：SDK 10.0.300；WindowsDesktop Runtime 8.0.27 / 10.0.8 在位 → **目标框架 net10.0-windows**。

替代方案否决记录：Tauri2+Rust（需装 Rust 1.5GB+全逻辑跨语言重写）、Wails v3（alpha 稳定性）、
Go+WebView2（WebView2 多进程内存反而更高）。

| 维度 | 原项目 | 新项目 |
|---|---|---|
| 进程 | 3 exe 互相守望 | 1 exe |
| 内存 | ~250-400MB | 目标 ~80-130MB |
| 磁盘 | 3 exe+依赖 | exe 2-5MB（框架依赖发布）+ 依赖 DLL |
| 运行时 | WebView2+.NET | 仅 .NET Desktop Runtime 10（已有） |

## 2. 解决方案结构（骨架已建）

```
Token Monitor/                      # D:\Zcode project\Token Monitor
├─ TokenMonitor.slnx
├─ src/TokenMonitor.Core/           # 核心类库 net10.0，无 UI 依赖，全部可单测
│   ├─ Proxy/      HttpListener 反代+捕获+请求整备
│   ├─ Parser/     多厂商用量归一化
│   ├─ Stats/      双口径线程安全累积器+不可变快照
│   ├─ Pricing/    倍率+计价引擎（版本化/双基准/分数小时）
│   ├─ Storage/    SQLite(Microsoft.Data.Sqlite)+XLSX(MiniExcel)+文件日志+备份回滚
│   ├─ Config/     JSON 配置+DPAPI 密钥加密
│   ├─ SysUtil/    单实例 Mutex/自启注册表/时区表/日志
│   └─ Events/     进程内事件总线
├─ src/TokenMonitor.App/            # WPF net10.0-windows
│   ├─ Windows/    MainWindow(无边框面板)+FloatingBall(透明分层窗)
│   ├─ Controls/   模型卡/环形图/折线图/比例条/流量动效（矢量自绘）
│   ├─ Dialogs/    7 个配置对话框
│   ├─ Tray/       Hardcodet NotifyIcon+全套菜单
│   ├─ Themes/     UI-Designer 定稿设计令牌 ResourceDictionary
│   ├─ Services/   状态持久化/旧数据导入向导
│   └─ ViewModels/ CommunityToolkit.Mvvm
└─ tests/TokenMonitor.Core.Tests/   # xUnit：移植原 Go 测试+19 项 Bug 回归
```

NuGet（已装）：Microsoft.Data.Sqlite、MiniExcel、Hardcodet.NotifyIcon.Wpf、CommunityToolkit.Mvvm。

## 3. 线程与通信模型

- Core 全后台异步：代理捕获（async/await，客户端断开后续读任务分离）、批量刷写、重算。
- **UI↔Core 进程内事件总线直连**（取消原全部 /api HTTP）：
  - `StatsSnapshot`（不可变）每 200ms 推面板、1s 推悬浮球；
  - `ConfigChanged`（pricing/settings/providers 变更广播，UI 缓存失效）；
  - `DayRolledOver`（跨午夜/时区切换后重建完成通知）；
  - `ImportCompleted` 等一次性事件。
- 配置变更：锁内"旧快照→新累积器→DB 回放间隙"（修 C5）。
- HTTP 仅保留 `/v1/*`、`/v1/models`、`/health`（127.0.0.1）。

## 4. 运行期数据布局（exe 旁 data/，.gitignore 已排除）

```
data/
├─ token_monitor.db       # 新库（表语义对齐旧库便于导入）
├─ config.json            # providers（api_key 支持 dpapi: 前缀密文）
├─ pricing.json           # 倍率/计价版本链（字段与原版兼容）
├─ settings.json          # offset_min/effective_date_mode/ball_opacity/ball_topmost
├─ ui_state.json          # 面板窗口/卡片顺序/隐藏卡片/每卡口径/倍率开关/球设置
├─ usage_logs/            # YYYY-MM-DD_Provider_Model.log/.csv
├─ backups/               # 校准/导入自动备份 (VACUUM INTO)
├─ export/                # XLSX 导出
└─ logs/TokenMonitor.log  # 运行日志（5MB 截断）
```

## 5. UI 实施约束（重要）

1. **主面板与悬浮球的视觉/交互不得参照原项目样式**（原项目为"霓虹青橙紫玻璃 HUD"）。
   一切以 UI-Designer 产出、经用户审核选定的定稿设计规范为准实施。
2. 信息架构（显示哪些数据、哪些功能入口）按 01-original-features.md §3-D/E/F 不变。
3. WPF 技术硬约束：不可用 backdrop-blur（WPF 无法对桌面内容模糊）；可用渐变/阴影/辉光
   (DropShadowEffect)/圆角裁剪/透明分层窗口/Storyboard/矢量绘制；注意常驻小部件的克制与可读性。
4. 中文文案；深色基调为默认（常驻场景）；数字用等宽/表格数字对齐。

## 6. 明确排除（不移植）

DataViewerDialog、ProgressBars（原前端死代码）；ntm-migrate、tools/fix_history（一次性修复工具，
由 G1 导入功能取代）；docs 生成器（改为 README）。死解析器代码（C18）不移植。

## 7. 安全红线

- 不把明文 API 密钥写入任何新文件/日志/git；新配置默认要求用户手填，密钥 DPAPI 加密落盘。
- data/、*.db、logs 全部进 .gitignore（已配）。
- HTTP 面只监听 127.0.0.1；无任何可远程触发的破坏性接口。
