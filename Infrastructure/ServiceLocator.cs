namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 轻量服务定位器（无 DI 容器）。App 启动时注册全部服务实现；
/// 视图通过 <see cref="Get{T}"/> 取用，未注册返回 null（视图自行降级展示）。
/// </summary>
public static class ServiceLocator
{
    private static readonly Dictionary<Type, object> Services = new();

    public static void Register<TInterface>(object implementation) where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(implementation);
        Services[typeof(TInterface)] = implementation;
    }

    public static TInterface? Get<TInterface>() where TInterface : class
    {
        return Services.TryGetValue(typeof(TInterface), out var impl) ? impl as TInterface : null;
    }

    public static bool Has<TInterface>() where TInterface : class
    {
        return Services.ContainsKey(typeof(TInterface));
    }

    /// <summary>清空（测试或重载场景用）。</summary>
    public static void Reset() => Services.Clear();
}
