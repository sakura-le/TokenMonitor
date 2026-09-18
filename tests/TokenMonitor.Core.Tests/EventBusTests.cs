using System.Collections.Concurrent;
using TokenMonitor.Core.Events;

namespace TokenMonitor.Core.Tests;

/// <summary>事件总线行为回归（FIFO/合并/异常隔离/退订/分发模式）。</summary>
public class EventBusTests
{
    private sealed record Ping(int N) : IEvent;
    private sealed record Boom : IEvent;

    [Fact]
    public async Task EventBus_FifoOrder_Preserved()
    {
        var bus = new EventBus();
        var seen = new ConcurrentQueue<int>();
        using var sub = bus.Subscribe<Ping>(e => seen.Enqueue(e.N));
        bus.Publish(new Ping(1));
        bus.Publish(new Ping(2));
        await TestHelpers.WaitForAsync(() => seen.Count == 2, message: "FIFO 分发未完成");
        Assert.Equal([1, 2], seen.ToArray()); // FIFO 顺序
    }

    [Fact]
    public async Task EventBus_Coalesced_KeepsLatest()
    {
        // 快照类合并发布：处理器阻塞时中间载荷被原地替换，永只处理最新（背压=仅保留最新）
        var bus = new EventBus();
        var seen = new ConcurrentQueue<int>();
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe<Ping>(e =>
        {
            seen.Enqueue(e.N);
            if (e.N == 1)
            {
                firstSeen.TrySetResult();
#pragma warning disable xUnit1031 // 模拟 UI 阻塞：订阅者必须同步挂住才能验证合并语义
                release.Task.Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
            }
            else
            {
                lastSeen.TrySetResult(e.N);
            }
        });
        bus.PublishCoalesced(new Ping(1));
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bus.PublishCoalesced(new Ping(2));
        bus.PublishCoalesced(new Ping(3)); // 2 尚未分发 → 被原地替换为 3
        release.TrySetResult();
        Assert.Equal(3, await lastSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await TestHelpers.WaitForAsync(() => seen.Count == 2, message: "合并分发未完成");
        Assert.Equal([1, 3], seen.ToArray()); // 2 被合并丢弃
    }

    [Fact]
    public async Task EventBus_HandlerException_Isolated()
    {
        var bus = new EventBus();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bad = bus.Subscribe<Boom>(_ => throw new InvalidOperationException("boom"));
        using var good = bus.Subscribe<Boom>(_ => tcs.TrySetResult(true));
        bus.Publish(new Boom()); // bad 抛异常 → 隔离；good 仍收到
        Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EventBus_Unsubscribe_Idempotent()
    {
        var bus = new EventBus();
        var count = 0;
        var sub = bus.Subscribe<Ping>(_ => Interlocked.Increment(ref count));
        bus.Publish(new Ping(1));
        await TestHelpers.WaitForAsync(() => Volatile.Read(ref count) == 1);
        sub.Dispose();
        sub.Dispose(); // 幂等
        bus.Publish(new Ping(2));
        await Task.Delay(100);
        Assert.Equal(1, count); // 退订后不再接收
    }

    [Fact]
    public void EventBus_Publish_NeverThrows_NeverBlocks()
    {
        var bus = new EventBus();
        // 无订阅者/异常订阅者/慢订阅者下发布均不抛不阻塞（慢订阅者异步处理）
        bus.Publish(new Ping(1));
        bus.PublishCoalesced(new Ping(2));
        using var bad = bus.Subscribe<Ping>(_ => throw new InvalidOperationException("x"));
        bus.Publish(new Ping(3));
    }

    [Fact]
    public void EventBus_PublisherThread_Dispatch_Synchronous()
    {
        var bus = new EventBus();
        var threadId = 0L;
        using var sub = bus.Subscribe<Ping>(_ => threadId = Environment.CurrentManagedThreadId,
            EventDispatch.PublisherThread);
        var publishThreadId = Environment.CurrentManagedThreadId;
        bus.Publish(new Ping(1));
        Assert.Equal(publishThreadId, threadId); // 发布线程内同步执行
    }
}
