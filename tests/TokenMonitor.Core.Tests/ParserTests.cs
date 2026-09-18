using TokenMonitor.Core.Parser;

namespace TokenMonitor.Core.Tests;

/// <summary>Parser 回归：原 parser_test 移植 + C3/C18 相关铁律 + D2 差异加固。</summary>
public class ParserTests
{
    private readonly UsageParser _p = new();

    [Fact]
    public void Parser_ParseSseDataLine_ContentBlock_ReturnsNoUsage()
    {
        // 原 parser_test 用例 1 前置：普通内容块 → Usage=null
        var r = _p.ParseSseDataLine("""{"model":"m","choices":[{"delta":{"content":"hi"}}]}""", "P");
        Assert.Null(r.Usage);
    }

    [Fact]
    public void Parser_ParseSseDataLine_DeepSeekStyleUsage_Normalizes()
    {
        // 原 parser_test 用例 1：DeepSeek 风格 hit/miss 分字段
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10,"completion_tokens":50,"completion_tokens_details":{"reasoning_tokens":20},"total_tokens":150}}""",
            "P");
        Assert.NotNull(r.Usage);
        var u = r.Usage!.Value;
        Assert.Equal(100, u.PromptTokens);
        Assert.Equal(90, u.CacheHitTokens);
        Assert.Equal(10, u.CacheMissTokens);
        Assert.Equal(50, u.CompletionTokens);
        Assert.Equal(20, u.ReasoningTokens);
        Assert.Equal(150, u.TotalTokens);
    }

    [Fact]
    public void Parser_ParseSseDataLine_MissDerivedWhenAbsent()
    {
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"completion_tokens":50,"total_tokens":150}}""", "P");
        Assert.NotNull(r.Usage);
        Assert.Equal(10, r.Usage!.Value.CacheMissTokens);
    }

    [Fact]
    public void Parser_ParseSseDataLine_MalformedJson_ReturnsNull()
    {
        var r = _p.ParseSseDataLine("""{"model":""", "P");
        Assert.Null(r.Usage);
    }

    [Fact]
    public void Parser_ParseSseDataLine_DashScopeAlias_MapsAndDerives()
    {
        // 原 parser_test 用例 2：input_tokens_details.cached_tokens → hit=150、miss=50、total=250
        var r = _p.ParseSseDataLine(
            """{"model":"qwen","usage":{"input_tokens":200,"input_tokens_details":{"cached_tokens":150},"output_tokens":50,"total_tokens":250}}""", "P");
        Assert.NotNull(r.Usage);
        var u = r.Usage!.Value;
        Assert.Equal(200, u.PromptTokens);
        Assert.Equal(50, u.CompletionTokens);
        Assert.Equal(150, u.CacheHitTokens);
        Assert.Equal(50, u.CacheMissTokens);
        Assert.Equal(250, u.TotalTokens);
    }

    [Fact]
    public void Parser_ParseSseDataLine_DashScopeAlias_DerivesTotal()
    {
        var r = _p.ParseSseDataLine(
            """{"model":"qwen","usage":{"input_tokens":100,"output_tokens":20}}""", "P");
        Assert.NotNull(r.Usage);
        var u = r.Usage!.Value;
        Assert.Equal(100, u.PromptTokens);
        Assert.Equal(20, u.CompletionTokens);
        Assert.Equal(120, u.TotalTokens);
    }

    [Fact]
    public void Parser_ExplicitMiss_ConflictingWithIdentity_Overridden()
    {
        // 防线 2：显式 miss 与恒等式冲突 → 一律推翻重推导
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":20,"total_tokens":150}}""", "P");
        Assert.NotNull(r.Usage);
        Assert.Equal(10, r.Usage!.Value.CacheMissTokens);
    }

    [Fact]
    public void Parser_HitExceedPrompt_Clamped()
    {
        // 防线 1（D2-2）：hit > prompt 视为坏数据 → hit 钳到 prompt，miss 归 0
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":50,"prompt_cache_hit_tokens":80,"total_tokens":100}}""", "P");
        Assert.NotNull(r.Usage);
        var u = r.Usage!.Value;
        Assert.Equal(50, u.CacheHitTokens);
        Assert.Equal(0, u.CacheMissTokens);
    }

    [Fact]
    public void Parser_TopLevelReasoningTokens_Alias()
    {
        // D2-1：顶层 reasoning_tokens 别名（原版仅 completion_tokens_details）
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":10,"completion_tokens":5,"reasoning_tokens":3,"total_tokens":15}}""", "P");
        Assert.NotNull(r.Usage);
        Assert.Equal(3, r.Usage!.Value.ReasoningTokens);
    }

    [Fact]
    public void Parser_StringNumberTolerance()
    {
        // D2-4：字符串数值容错（原 Go 整包失败 → 漏抓；新实现字段级解析）
        var r = _p.ParseSseDataLine(
            """{"model":"m","usage":{"prompt_tokens":"100","completion_tokens":"50","total_tokens":150}}""", "P");
        Assert.NotNull(r.Usage);
        Assert.Equal(100, r.Usage!.Value.PromptTokens);
        Assert.Equal(50, r.Usage!.Value.CompletionTokens);
    }

    [Fact]
    public void Parser_ZeroUsage_StillCapturedWithZeros()
    {
        // 02-§2.3：全零 usage → 事件仍捕获（RequestCount+1 由消费方保证，此处验证归一化不判空）
        var r = _p.ParseSseDataLine("""{"model":"m","usage":{}}""", "P");
        Assert.NotNull(r.Usage);
        var u = r.Usage!.Value;
        Assert.Equal(0, u.PromptTokens);
        Assert.Equal(0, u.TotalTokens);
    }

    [Fact]
    public void Parser_JsonResponse_NoUsageField_ReturnsNullWithoutReason()
    {
        var r = _p.ParseJsonResponse("""{"model":"m","choices":[]}""", "P");
        Assert.Null(r.Usage);
        Assert.Null(r.FailureReason);
    }

    [Fact]
    public void Parser_JsonResponse_Malformed_ReturnsFailureReason()
    {
        var r = _p.ParseJsonResponse("""{"model":""", "P");
        Assert.Null(r.Usage);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public void Parser_TotalUnification_AllConsumersUseSameField()
    {
        // [C3] total 铁律：厂商 total>0 时取厂商值（即使与 prompt+completion 不一致），全链路唯一来源
        var r = _p.ParseJsonResponse(
            """{"model":"m","usage":{"prompt_tokens":500,"completion_tokens":600,"total_tokens":999}}""", "P");
        Assert.NotNull(r.Usage);
        Assert.Equal(999, r.Usage!.Value.TotalTokens); // 厂商值胜出（prompt+completion=1100 被弃用）
    }

    [Fact]
    public void Parser_TotalMissing_DerivedFromPromptPlusCompletion()
    {
        var r = _p.ParseJsonResponse(
            """{"model":"m","usage":{"prompt_tokens":500,"completion_tokens":600}}""", "P");
        Assert.Equal(1100, r.Usage!.Value.TotalTokens);
    }

    [Theory]
    [InlineData("application/json;charset=utf-8", true)]
    [InlineData("application/json", true)]
    [InlineData("text/plain", false)]
    [InlineData(null, true)] // 缺 CT 且 body 为 {. → JSON（D1-2）
    public void Parser_IsJsonRequestBody_ContentTypeDetection(string? contentType, bool expected)
    {
        Assert.Equal(expected, _p.IsJsonRequestBody(contentType, "{}"u8));
    }

    [Fact]
    public void Parser_IsJsonRequestBody_MissingContentType_BracePrefixDetected()
    {
        // D1-2：缺 CT 且 body 首个非空白字符为 '{' → 视为 JSON
        Assert.True(_p.IsJsonRequestBody(null, " \r\n {\"model\":\"x\"}"u8));
        Assert.False(_p.IsJsonRequestBody(null, "--boundary"u8));
    }

    [Fact]
    public void Parser_C18_DeadParserTypesNotPorted()
    {
        // [C18] 死解析器（TeeReaderSplit/ScanSSEData）未移植——编译期保障：类型不存在
        Assert.DoesNotContain(typeof(UsageParser).Assembly.GetTypes(), t => t.Name.Contains("TeeReaderSplit"));
        Assert.DoesNotContain(typeof(UsageParser).Assembly.GetTypes(), t => t.Name.Contains("ScanSSEData"));
    }
}
