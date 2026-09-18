namespace TokenMonitor.Core;

/// <summary>代理启动失败（端口占用 / 非回环地址 / Kestrel 绑定失败）。Engine 捕获后不终止进程（附录 B-9）。</summary>
public sealed class ProxyStartException : Exception
{
    public ProxyStartException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>IStore 查询/维护方法失败时抛出；写路径内部自吞（§8.1）。</summary>
public sealed class StorageException : Exception
{
    public StorageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>IConfigService.Save* 失败时抛出；加载路径永不抛（§3 修复策略）。</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>旧数据导入失败时抛出（导入事务已回滚，新库保持原状）。</summary>
public sealed class ImportException : Exception
{
    public ImportException(string message, Exception? inner = null) : base(message, inner) { }
}
