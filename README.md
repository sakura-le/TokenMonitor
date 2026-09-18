# Token Monitor

Windows 桌面常驻工具：监控本机 LLM API 代理流量的 Token 用量与费用。
（WPF + XAML 单进程架构，全盘移植自原 neon-token-monitor 并修复全部已审计缺陷）

> 本文件为占位文档，交付阶段将补全：构建/运行说明、配置说明、旧数据导入说明。

- 设计文档：`design/`（功能清单、Bug 审计、架构决策、UI 样例与定稿规范）
- 核心逻辑：`src/TokenMonitor.Core/`
- WPF 主程序：`src/TokenMonitor.App/`
- 测试：`tests/TokenMonitor.Core.Tests/`
