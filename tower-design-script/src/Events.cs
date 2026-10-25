namespace TowerGrid.Core;

/// <summary>
/// 内置事件名常量。事件系统是"开放字符串"的：mod 可以发布/订阅任意新事件名，
/// 防御塔蓝图的技力事件回复表（SkillSpec.SpOnEvent）同样可以直接引用新事件名。
/// 约定：SimEvent.Tower 为事件主语塔（如 tower.attack 的攻击者、tower.damaged 的受害者、enemy.death 的击杀者），
/// 技力事件回复发给主语塔。
/// </summary>
public static class GameEvents
{
    /// <summary>每个仿真步发布一次（无主语）。</summary>
    public const string Tick = "tick";

    public const string TowerPlaced = "tower.placed";
    public const string TowerRemoved = "tower.removed";
    public const string TowerAttack = "tower.attack";       // 塔出手（普攻或技能，Data="normal"/"skill"）
    public const string TowerDamaged = "tower.damaged";     // 塔被敌人攻击（主语 = 受害塔）
    public const string TowerDestroyed = "tower.destroyed";
    public const string SkillTriggered = "tower.skill";     // 技能释放

    public const string EnemySpawn = "enemy.spawn";
    public const string EnemyHit = "enemy.hit";             // 敌人被伤害（主语 = 攻击者塔）
    public const string EnemyDeath = "enemy.death";         // 主语 = 击杀者塔
    public const string EnemyStunned = "enemy.stunned";
    public const string EnemyLeak = "enemy.leak";           // 敌人抵达终点，基地扣血

    /// <summary>弹道发射（主语 = 发射塔；TargetCell = 落点/目标当时所在格）。</summary>
    public const string ProjectileLaunch = "projectile.launch";

    /// <summary>弹道命中/落地，作用区间开始结算（Cell = 锚点格；无论区间内是否有目标都发布）。</summary>
    public const string ProjectileImpact = "projectile.impact";

    /// <summary>召唤吟唱开始（主语 = 召唤塔；TargetCell = 首个落区格）；效果将在 Delay 后落地。</summary>
    public const string SummonCast = "summon.cast";
}

/// <summary>事件载荷。Tower/Enemy/TargetTower/Cell/Amount 按事件语义填充，Data 放扩展内容。</summary>
public sealed class SimEvent
{
    public required string Name { get; init; }
    public double Time { get; init; }
    public Tower? Tower { get; init; }
    public Enemy? Enemy { get; init; }
    public Tower? TargetTower { get; init; }
    public CellPos Cell { get; init; }
    public CellPos TargetCell { get; init; }
    public double Amount { get; init; }
    public string? Message { get; init; }
    public object? Data { get; init; }
}

/// <summary>
/// 全局事件总线（发布/订阅）。订阅者可以来自任何地方 —— 这是 mod 挂钩游戏过程的主要入口之一。
/// 订阅 "*" 可收到所有事件（调试/埋点用）。
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<string, List<Action<SimEvent>>> _subs = new(StringComparer.Ordinal);

    public IDisposable Subscribe(string eventName, Action<SimEvent> handler)
    {
        if (!_subs.TryGetValue(eventName, out var list))
            _subs[eventName] = list = [];
        list.Add(handler);
        return new Unsub(this, eventName, handler);
    }

    public void Publish(SimEvent e)
    {
        if (_subs.TryGetValue(e.Name, out var list))
            PublishTo(list, e);
        if (_subs.TryGetValue("*", out var all))
            PublishTo(all, e);
    }

    private static void PublishTo(List<Action<SimEvent>> list, SimEvent e)
    {
        // 倒序遍历：允许订阅者在回调里退订自身
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (i >= list.Count)
                continue;
            list[i](e);
        }
    }

    private sealed class Unsub(EventBus bus, string name, Action<SimEvent> handler) : IDisposable
    {
        public void Dispose()
        {
            if (bus._subs.TryGetValue(name, out var list))
                list.Remove(handler);
        }
    }
}
