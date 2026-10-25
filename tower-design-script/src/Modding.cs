namespace TowerGrid.Core;

/// <summary>
/// 蓝图注册表：防御塔蓝图按 Id 注册，mod 加新塔 = 注册一份新蓝图。
/// 演示 UI 直接遍历 All 生成"可建造列表"（卸载即时反映到界面）。
/// </summary>
public static class TowerLibrary
{
    private static readonly Dictionary<string, TowerBlueprint> Map = new(StringComparer.Ordinal);

    public static void Register(TowerBlueprint bp) => Map[bp.Id] = bp;

    /// <summary>注销一个塔蓝图（mod 卸载用）。已在场上的实例持有蓝图引用，不受影响，只是不能再新建。</summary>
    public static bool Unregister(string id) => Map.Remove(id);

    public static TowerBlueprint Get(string id) =>
        Map.TryGetValue(id, out var bp) ? bp : throw new KeyNotFoundException($"未注册的塔蓝图：{id}");

    public static bool TryGet(string id, out TowerBlueprint? bp) => Map.TryGetValue(id, out bp);

    public static IReadOnlyCollection<TowerBlueprint> All => Map.Values;
}

/// <summary>敌人蓝图注册表（波次与刷怪按 Id 引用）。</summary>
public static class EnemyLibrary
{
    private static readonly Dictionary<string, EnemyBlueprint> Map = new(StringComparer.Ordinal);

    public static void Register(EnemyBlueprint bp) => Map[bp.Id] = bp;

    public static bool Unregister(string id) => Map.Remove(id);

    public static EnemyBlueprint Get(string id) =>
        Map.TryGetValue(id, out var bp) ? bp : throw new KeyNotFoundException($"未注册的敌人蓝图：{id}");

    public static IReadOnlyCollection<EnemyBlueprint> All => Map.Values;
}

/// <summary>
/// 防御塔内容模组：把一组塔 / 敌人 / 效果处理器 / 事件挂线捆绑为一个可整体装卸的单元。
/// - C# 模组：直接 new TowerMod { ... }（可携带 EffectHandlers 代码与 OnWire 事件挂线）；
/// - 数据模组：由 TowerModJson.Parse 从 JSON 文件构造（仅数据，效果 Kind 必须已注册）。
/// 装载/卸载是运行时操作：演示 UI 的工具栏遍历 TowerLibrary.All，即时反映。
/// </summary>
public sealed class TowerMod
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0";

    public IReadOnlyList<TowerBlueprint> Towers { get; init; } = [];
    public IReadOnlyList<EnemyBlueprint> Enemies { get; init; } = [];

    /// <summary>本模组注册的效果处理器（仅 C# 模组可携带；JSON 模组引用已注册的 Kind）。</summary>
    public IReadOnlyList<(string Kind, EffectRegistry.Handler Handler)> EffectHandlers { get; init; } = [];

    /// <summary>
    /// 每局开始时由宿主调用（传入新 Simulation）—— mod 的事件订阅挂在这里，
    /// 订阅随该局的 EventBus 一起销毁，无需手动退订。
    /// </summary>
    public Action<Simulation>? OnWire { get; init; }

    public bool Installed { get; private set; }

    private static readonly List<TowerMod> _installed = [];

    /// <summary>当前已装载的全部模组（含核心内容模组）。</summary>
    public static IReadOnlyList<TowerMod> InstalledMods => _installed;

    public void Install()
    {
        if (Installed)
            return;
        foreach (var t in Towers)
            TowerLibrary.Register(t);
        foreach (var e in Enemies)
            EnemyLibrary.Register(e);
        foreach (var (kind, handler) in EffectHandlers)
            EffectRegistry.Register(kind, handler);
        _installed.Add(this);
        Installed = true;
    }

    public void Uninstall()
    {
        if (!Installed)
            return;
        foreach (var t in Towers)
            TowerLibrary.Unregister(t.Id);
        foreach (var e in Enemies)
            EnemyLibrary.Unregister(e.Id);
        foreach (var (kind, _) in EffectHandlers)
            EffectRegistry.Unregister(kind);
        _installed.Remove(this);
        Installed = false;
    }
}
