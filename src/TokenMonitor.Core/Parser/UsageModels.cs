using System.Text.Json;

namespace TokenMonitor.Core.Parser;

/// <summary>
/// 归一化用量（01-§2.2）：全链路唯一用量载体。
/// TotalTokens 在此处一次性定型（铁律 1 / [C3]）：厂商 total&gt;0 取厂商值，否则 prompt+completion。
/// 恒等式 Prompt = CacheHit + CacheMiss 恒成立（原 normalizeUsage 末段收口保持）。
/// </summary>
public readonly record struct UnifiedUsage(
    long PromptTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    long CompletionTokens,
    long ReasoningTokens,
    long TotalTokens);

/// <summary>捕获来源。</summary>
public enum CaptureSource { Sse, Json, Estimated }

/// <summary>一笔已归一化的用量事件。CompletedAtUnix = 响应完成时刻（铁律 4）。</summary>
public sealed record UsageEvent(string Provider, string Model, UnifiedUsage Usage,
                                long CompletedAtUnix, CaptureSource Source)
{
    /// <summary>模型键（铁律 3，只读计算属性，不参与 record 相等性）。</summary>
    public ModelKey Key => new(Provider, Model);
}

/// <summary>解析结果（不抛异常，漏抓原因由此带回）。</summary>
public sealed record ParseResult(UnifiedUsage? Usage, string? FailureReason);

/// <summary>多厂商 usage 归一化器（纯函数、无 IO、无状态、任意线程）。</summary>
public interface IUsageParser
{
    /// <summary>解析非流式 JSON 响应体。Usage=null 表示无 usage 字段；JSON 结构非法时 FailureReason 带原因。永不抛异常。</summary>
    ParseResult ParseJsonResponse(string responseBody, string provider);

    /// <summary>解析单行 SSE data 载荷（不含 "data: " 前缀）。该行不含 "usage" 或不可解析 → Usage=null。</summary>
    ParseResult ParseSseDataLine(string dataLine, string provider);

    /// <summary>请求体是否为可捕获的 JSON（[C2]：决定走"整备+捕获"还是"纯透传"路径）。</summary>
    bool IsJsonRequestBody(string? contentType, ReadOnlySpan<byte> bodyPrefix);
}
