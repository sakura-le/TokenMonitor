namespace TokenMonitor.Core.Events;

/// <summary>事件标记接口（01-§2.3.6）。</summary>
public interface IEvent { }

/// <summary>分发模式：发布线程同步 / 订阅时捕获的 UI SynchronizationContext / 线程池。</summary>
public enum EventDispatch { PublisherThread, UiThread, BackgroundPool }

/// <summary>
/// 进程内事件总线：类型化发布/订阅；Publish=FIFO（一次性事件，有界队列 32，溢出丢最旧并告警）；
/// PublishCoalesced=合并式（快照类，未分发的旧载荷被原地替换，UI 永远只处理最新）。
/// Publish/Subscribe 均线程安全；Publish 永不阻塞、永不抛；handler 内异常被隔离。
/// </summary>
public interface IEventBus
{
    /// <summary>订阅。UiThread 模式捕获订阅时的 SynchronizationContext（必须已在 UI 线程订阅）；
    /// 无 SyncContext 时降级为线程池分发。返回 IDisposable；Dispose=退订（可重入、幂等）。</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventDispatch dispatch = EventDispatch.BackgroundPool)
        where TEvent : IEvent;

    /// <summary>FIFO 发布（每主题有界队列 32，溢出丢最旧并 Warn）。</summary>
    void Publish<TEvent>(TEvent evt) where TEvent : IEvent;

    /// <summary>合并式发布：若该主题尚有未分发的旧载荷则原地替换（背压=仅保留最新）。</summary>
    void PublishCoalesced<TEvent>(TEvent evt) where TEvent : IEvent;
}
