using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Events;

/// <summary>
/// EventBus 默认实现。每事件类型一条投递通道：
///  - PublisherThread 订阅者在 Publish 调用线程内同步执行；
///  - UiThread 订阅者经订阅时捕获的 SynchronizationContext.Post 分发；
///  - BackgroundPool 订阅者经线程池分发。
/// FIFO 主题有界队列 32（溢出丢最旧+告警）；合并主题单槽位（最新者胜）。
/// 发布方在锁外组装/调用 handler；任一 handler 异常被捕获并记 Warn，不影响其它订阅者。
/// </summary>
public sealed class EventBus : IEventBus
{
    private const int FifoCapacity = 32;

    private sealed class Subscription
    {
        public required EventDispatch Dispatch;
        public SynchronizationContext? UiContext;
        public required Action<object> Invoke;
    }

    private sealed class Topic
    {
        public readonly object Gate = new();
        public List<Subscription> Subs = new();
        public readonly Queue<object> Fifo = new();
        public object? CoalescedSlot;
        public bool Dispatching;
    }

    private readonly Dictionary<Type, Topic> _topics = new();
    private readonly object _topicsGate = new();

    private Topic TopicOf(Type type)
    {
        lock (_topicsGate)
        {
            if (!_topics.TryGetValue(type, out var t))
            {
                t = new Topic();
                _topics[type] = t;
            }
            return t;
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventDispatch dispatch = EventDispatch.BackgroundPool)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var sub = new Subscription
        {
            Dispatch = dispatch,
            UiContext = dispatch == EventDispatch.UiThread ? SynchronizationContext.Current : null,
            Invoke = o => handler((TEvent)o),
        };
        var topic = TopicOf(typeof(TEvent));
        lock (topic.Gate)
        {
            topic.Subs.Add(sub);
        }
        return new Unsubscription(this, typeof(TEvent), sub);
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(evt);
        var topic = TopicOf(typeof(TEvent));
        List<Subscription> subs;
        lock (topic.Gate)
        {
            subs = topic.Subs.ToList();
            var needsQueue = subs.Any(s => s.Dispatch != EventDispatch.PublisherThread);
            if (needsQueue)
            {
                if (topic.Fifo.Count >= FifoCapacity)
                {
                    topic.Fifo.Dequeue();
                    Logger.Warn("Events", $"{typeof(TEvent).Name} 队列已满，丢弃最旧载荷");
                }
                topic.Fifo.Enqueue(evt);
                EnsureDispatching(topic);
            }
        }
        // PublisherThread 订阅者在发布线程内同步执行（锁外）
        foreach (var s in subs.Where(s => s.Dispatch == EventDispatch.PublisherThread))
            SafeInvoke(s, evt);
    }

    public void PublishCoalesced<TEvent>(TEvent evt) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(evt);
        var topic = TopicOf(typeof(TEvent));
        lock (topic.Gate)
        {
            topic.CoalescedSlot = evt;
            EnsureDispatching(topic);
        }
    }

    private static void EnsureDispatching(Topic topic)
    {
        if (topic.Dispatching) return;
        topic.Dispatching = true;
        _ = Task.Run(() => DispatchLoop(topic));
    }

    private static void DispatchLoop(Topic topic)
    {
        while (true)
        {
            List<Subscription> subs;
            object? payload;
            lock (topic.Gate)
            {
                if (topic.Fifo.Count > 0) payload = topic.Fifo.Dequeue();
                else if (topic.CoalescedSlot is not null) { payload = topic.CoalescedSlot; topic.CoalescedSlot = null; }
                else { topic.Dispatching = false; return; }
                subs = topic.Subs.ToList();
            }
            if (payload is null) continue;
            foreach (var sub in subs)
            {
                switch (sub.Dispatch)
                {
                    case EventDispatch.PublisherThread:
                        // 发布线程模式在队列通道中按后台执行（该主题未混用两种发布时不会出现）
                        SafeInvoke(sub, payload);
                        break;
                    case EventDispatch.UiThread when sub.UiContext is not null:
                        sub.UiContext.Post(_ => SafeInvoke(sub, payload), null);
                        break;
                    default:
                        SafeInvoke(sub, payload);
                        break;
                }
            }
        }
    }

    private static void SafeInvoke(Subscription sub, object payload)
    {
        try
        {
            sub.Invoke(payload);
        }
        catch (Exception ex)
        {
            Logger.Warn("Events", $"事件处理器异常（已隔离）: {ex.Message}");
        }
    }

    private sealed class Unsubscription(EventBus bus, Type type, Subscription sub) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var topic = bus.TopicOf(type);
            lock (topic.Gate)
            {
                topic.Subs.Remove(sub);
            }
        }
    }
}
