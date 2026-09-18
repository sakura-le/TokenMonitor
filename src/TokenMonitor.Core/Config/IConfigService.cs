using System.Security.Cryptography;
using System.Text.Json;

namespace TokenMonitor.Core.Config;

/// <summary>IConfigService 契约（01-§2.3.5）：JSON 配置读写与校验修复的唯一裁判。线程安全。</summary>
public interface IConfigService
{
    string DataDir { get; }
    ProxyConfig Proxy { get; }
    AppSettings Settings { get; }
    UiState Ui { get; }

    /// <summary>保存 config.json：明文 ApiKey → DPAPI 加密（"dpapi:&lt;b64&gt;"）；已是 dpapi: 的保持不变。失败抛 ConfigException，文件不动。</summary>
    void SaveProxy(ProxyConfig cfg);

    void SaveSettings(AppSettings s);

    /// <summary>防抖保存 ui_state.json（300ms 合并；进程退出时强制落盘）。</summary>
    void SaveUiState(UiState u);

    /// <summary>重读 config.json → ValidateAndRepair → 更新 Proxy；不抛（损坏时修复并告警日志）。</summary>
    void ReloadProxy();

    /// <summary>[S6] 校验修复（§3.1 校验表）。</summary>
    ProxyConfig ValidateAndRepair(ProxyConfig? raw);

    string EncryptApiKey(string plain);

    /// <summary>"dpapi:x" → 明文；无前缀原样返回（兼容旧明文配置，读取时告警一次）。</summary>
    string DecryptApiKey(string stored);

    /// <summary>内部文件保存完成事件（Engine 转发 ConfigChanged）。</summary>
    event EventHandler<ConfigSection>? Saved;
}
