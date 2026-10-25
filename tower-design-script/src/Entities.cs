namespace TowerGrid.Core;

/// <summary>场上实体基类（防御塔与敌人的最小公共面）。</summary>
public abstract class Entity
{
    /// <summary>运行时实例 ID（与蓝图 Id 区分）。</summary>
    public required string InstanceId { get; init; }

    /// <summary>挂在身上的事件盒（trigger_box 效果）：监听事件/倒计时，开盒时释放装填效果。</summary>
    public List<TrapBox> TrapBoxes { get; } = [];

    public abstract bool Alive { get; }
}

/// <summary>
/// 状态/属性增益数据。对防御塔是 buff，对敌人是"被注入的状态"，结构相同：
/// 效果处理器只负责写入（外部导入），含义由持有方的解释点定义，状态集合对 mod 开放。
/// Stat：塔用 attack / attack_speed；敌人用 stun / bind / silence / speed / attack / vulnerable / resist / dot / blind / mark…
/// Add 加算、Mul 乘算；Amount 为附加量（dot 的每秒伤害等）；Remaining 为剩余秒数。
/// </summary>
public sealed class Buff
{
    public required string Stat { get; init; }
    public double Add { get; init; }
    public double Mul { get; init; } = 1;
    public double Amount { get; init; }
    public double Remaining { get; set; }

    /// <summary>条件 ID（可选）：只在条件满足期间参与 Stat() 聚合（连续求值，随状态开关）。</summary>
    public string? Condition { get; init; }

    /// <summary>是否为属性规则（永不过期）。</summary>
    public bool Permanent { get; init; }
}

/// <summary>
/// 技能的运行时状态：每个技能一条独立技力条（Sp）、独立触发模式覆盖与替换模式剩余时间。
/// </summary>
public sealed class TowerSkill
{
    public required SkillSpec Spec { get; init; }

    public double Sp { get; set; }

    /// <summary>运行时覆盖技能触发模式（null = 沿用蓝图设定）。UI / mod 可随时切换自动 ⇄ 手动。</summary>
    public SkillTriggerMode? TriggerOverride { get; set; }

    /// <summary>实际生效的触发模式：运行时覆盖优先于蓝图。</summary>
    public SkillTriggerMode EffectiveTrigger => TriggerOverride ?? Spec.Trigger;

    /// <summary>替换普攻模式的剩余秒数（&gt;0 时本技能的 Action 取代普攻）。</summary>
    public double ModeRemaining { get; set; }

    public double SpMax => Spec.SpMax;

    public bool Ready => Sp >= Spec.SpMax - 1e-9;

    public void GainSp(double amount)
    {
        if (amount > 0)
            Sp = Math.Clamp(Sp + amount, 0, SpMax);
    }
}

/// <summary>
/// 弹道：攻击效果的投递载体（仅指向性 Homing/HomingRetarget 与目标性 Directional 投递方式产生）。
/// Homing（死守）每步朝原定目标飞、目标死即消散；HomingRetarget（择新）目标死时按原索敌规则转追；
/// Directional 沿发射向量飞往预定落点。命中/落地时经 Simulation 统一结算（快照的普攻倍率随弹道携带）。
/// </summary>
public sealed class Projectile
{
    public required string InstanceId { get; init; }
    public required Tower Source { get; init; }
    public required ActionSpec Action { get; init; }
    public required DeliverySpec Delivery { get; init; }

    /// <summary>是否作为技能投递（替换普攻模式的攻击按普攻节奏结算，但源头是技能）。</summary>
    public bool IsSkill { get; init; }

    /// <summary>发射时快照的 NextAttackMultiplier：普攻修饰符"发射即消耗"，命中时按快照注入结算。</summary>
    public double SnapshotMultiplier { get; init; }

    /// <summary>指向性（死守/择新）：当前跟踪的目标实体；目标性（Directional）：null。</summary>
    public Entity? HomingTarget { get; set; }

    /// <summary>Directional：预定落点格；Homing：发射时目标所在格（仅参考）。</summary>
    public CellPos ImpactCell { get; init; }

    /// <summary>作用区间形状的旋转基准（继承发射塔的朝向）。</summary>
    public Direction Facing { get; init; }

    /// <summary>当前位置（格单位中心坐标系：塔格中心 = Cell + (0.5,0.5)）。</summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>当前速度向量（格/秒）；Homing 每步重算，Directional 发射时固定。</summary>
    public double VX { get; set; }
    public double VY { get; set; }

    /// <summary>已飞行秒数（保险丝：超龄消散，防不可达目标挂死）。</summary>
    public double Age { get; set; }
}

/// <summary>
/// 待落地的召唤：召唤型攻击的延迟载体（非实体、非弹道——不设计无人机/召唤物，只是"延迟区域结算"）。
/// 发射时已锁定落区格（定区 = Area 全部格；定点 = 每个锁定目标的发射时格），
/// Delay 倒计时结束后交给作用区间系统结算——期间目标移动不影响落区（不跟随）。
/// </summary>
public sealed class PendingSummon
{
    public required string InstanceId { get; init; }
    public required Tower Source { get; init; }
    public required ActionSpec Action { get; init; }
    public required DeliverySpec Delivery { get; init; }
    public bool IsSkill { get; init; }

    /// <summary>发射时快照的普攻倍率（普攻修饰符"发射即消耗"，落地时注入结算）。</summary>
    public double SnapshotMultiplier { get; init; }

    /// <summary>落区格：定区 = Area 展开格；定点 = 锁定目标的发射时格（各配 Impact 区间落地）。</summary>
    public required IReadOnlyList<CellPos> Anchors { get; init; }

    /// <summary>作用区间的旋转基准（继承发射塔的朝向）。</summary>
    public Direction Facing { get; init; }

    /// <summary>距落地的剩余秒数。</summary>
    public double Remaining { get; set; }
}

/// <summary>防御塔运行时实例。数值大部分来自蓝图；本类只保存"会变"的状态。</summary>
public sealed class Tower : Entity
{
    public required TowerBlueprint Blueprint { get; init; }

    public CellPos Cell { get; set; }
    public Direction Facing { get; set; }

    /// <summary>当前射程档位（0 起，越界由 EffectiveAttackRange 夹取）。升级/效果/程序均可写。</summary>
    public int RangeTier { get; set; }

    public int RangeTierCount => Blueprint.RangeTiers?.Count ?? 0;

    /// <summary>当前生效的攻击范围：有档位表取当前档，否则取蓝图 AttackRange。</summary>
    public RangeShape EffectiveAttackRange =>
        Blueprint.RangeTiers is { Count: > 0 } tiers
            ? tiers[Math.Clamp(RangeTier, 0, tiers.Count - 1)]
            : Blueprint.AttackRange;

    public double Hp { get; set; }

    /// <summary>距下次普攻的剩余秒数。</summary>
    public double AttackCooldown { get; set; }

    /// <summary>TargetPick.Designated 的指定目标（塔↔塔能力的 1 对 1 表达，由玩家/程序显式提供）。</summary>
    public Tower? DesignatedTarget { get; set; }

    /// <summary>本塔的技能运行时状态列表（每个技能一条独立技力条），部署时由蓝图构建。</summary>
    public List<TowerSkill> Skills { get; } = [];

    /// <summary>当前生效的"替换普攻"技能（多个替换技能后触发覆盖先触发；模式结束时置回 null）。</summary>
    public TowerSkill? ActiveReplaceSkill { get; set; }

    public List<Buff> Buffs { get; } = [];

    /// <summary>强力击类效果：下次普攻的伤害倍率（普攻结算后重置为 1）。</summary>
    public double NextAttackMultiplier { get; set; } = 1;

    /// <summary>下次普攻的重复次数（普攻结算后清零）。</summary>
    public int MulticastStacks { get; set; }

    public override bool Alive => Hp > 0;

    public string Name => Blueprint.Name;
    public double MaxHp => Blueprint.MaxHp;
    public bool HasSkill => Skills.Count > 0;

    /// <summary>有效攻击力 =（基础 + buff 加算）× buff 乘算。</summary>
    public double EffectiveAttack
    {
        get
        {
            var (add, mul) = Stat("attack");
            return (Blueprint.AttackPower + add) * mul;
        }
    }

    /// <summary>有效攻击间隔（秒）。attack_speed 的 Mul 越大间隔越短。</summary>
    public double EffectiveInterval
    {
        get
        {
            var (_, mul) = Stat("attack_speed");
            var interval = Blueprint.AttackInterval * (mul <= 0 ? 1 : 1 / mul);
            return Math.Max(0.05, interval);
        }
    }

    public (double Add, double Mul) Stat(string stat)
    {
        double add = 0, mul = 1;
        foreach (var b in Buffs)
            if (b.Stat == stat && ConditionRegistry.Check(this, b.Condition))
            {
                add += b.Add;
                mul *= b.Mul;
            }
        return (add, mul);
    }
}

/// <summary>
/// 条件注册表：条件型属性修改的解释点（buff.Condition → 塔状态 → bool，连续求值）。
/// 条件 ID 形如 "hp_below:0.4"（冒号前为 ID，后为参数）；内置 hp_below:P / hp_above:P。
/// mod / 程序经 Register() 挂自定义条件（如"范围内存在易伤敌人"），未注册的 ID 恒为 false。
/// </summary>
public static class ConditionRegistry
{
    private static readonly Dictionary<string, Func<Tower, string?, bool>> Map = new()
    {
        // hp_below:0.4 = 本塔当前生命低于最大生命的 40%
        ["hp_below"] = (t, p) => double.TryParse(p, out var ratio) && t.Hp < t.MaxHp * Math.Clamp(ratio, 0, 1),
        // hp_above:0.6 = 本塔当前生命高于最大生命的 60%
        ["hp_above"] = (t, p) => double.TryParse(p, out var ratio) && t.Hp > t.MaxHp * Math.Clamp(ratio, 0, 1),
    };

    public static void Register(string id, Func<Tower, string?, bool> predicate) => Map[id] = predicate;

    /// <summary>条件 ID（含参数前缀）是否已注册（装载期校验用）。</summary>
    public static bool Known(string conditionId)
    {
        var id = conditionId.Split(':', 2)[0];
        return Map.ContainsKey(id);
    }

    public static bool Check(Tower tower, string? condition) =>
        condition is null
        || (Map.TryGetValue(condition.Split(':', 2)[0], out var f)
            && f(tower, condition.Contains(':') ? condition.Split(':', 2)[1] : null));
}

/// <summary>
/// 事件盒运行时实例：防御塔"发射"给目标的感知盒（trigger_box 效果产生）。
/// 监听 TriggerEvent（主语=携带者）或倒计时（TriggerEvent 为 null），
/// 满足依据时开盒——以携带者为锚对 Payload 区域结算装填的效果（效果可任意组合）。
/// </summary>
public sealed class TrapBox
{
    public required Entity Carrier { get; init; }
    /// <summary>发射盒子的塔：payload 结算的来源主语（UseSourceAttack 引用它的攻击力）。</summary>
    public required Tower Source { get; init; }
    public required ActionSpec Payload { get; init; }
    /// <summary>触发事件名（null = 定时盒，Remaining 归零即开盒）。</summary>
    public string? TriggerEvent { get; init; }
    /// <summary>定时盒倒计时 / 事件盒最长存在秒数（0 = 不限，直到触发）。</summary>
    public double Remaining { get; set; }
}

/// <summary>敌人运行时实例。沿路径以 PathDist（格）推进；状态由效果外部注入，本类只提供固定的解释点。</summary>
public sealed class Enemy : Entity
{
    public required EnemyBlueprint Blueprint { get; init; }
    public required GridMap Map { get; init; }

    public double Hp { get; set; }

    /// <summary>沿路径已走过的格距（0 = 出生点，Map.PathLength = 终点）。</summary>
    public double PathDist { get; set; }

    /// <summary>距下次攻击防御塔的剩余秒数（被阻挡时使用）。</summary>
    public double AttackCooldown { get; set; }

    /// <summary>本回合是否停驻（被塔阻挡 / 被控制 / 在排队）—— 后方敌人据此排队，不会全部叠在塔上。</summary>
    public bool StoppedThisTick { get; set; }

    /// <summary>被外部效果注入的状态列表（含义由下方解释点定义，集合对 mod 开放）。</summary>
    public List<Buff> Modifiers { get; } = [];

    /// <summary>DoT 结算计时器：每满 1 秒结算一次周期伤害。</summary>
    public double DoTTimer { get; set; } = 1;

    public override bool Alive => Hp > 0;

    // ------------------------------------------------------------
    // 解释点（固定承诺）：敌人"认识"的只有这几个取值点；
    // 任何 mod 都可以注入新状态名 —— 有解释点读取即生效，没有则静默到期。
    // ------------------------------------------------------------

    /// <summary>眩晕：完全失去行动（不动不攻击）。</summary>
    public bool IsStunned => HasActive("stun");

    /// <summary>束缚：不可移动，仍可攻击。</summary>
    public bool IsBound => HasActive("bind");

    /// <summary>沉默：禁用特殊能力（敌人暂无特殊能力，解释点为后续扩展预留）。</summary>
    public bool IsSilenced => HasActive("silence");

    /// <summary>有效移速 = 蓝图速度 × speed 类状态乘算（slow 在此生效）。</summary>
    public double EffectiveSpeed
    {
        get
        {
            var mul = 1.0;
            foreach (var m in Modifiers)
                if (m.Stat == "speed")
                    mul *= m.Mul;
            return Blueprint.Speed * mul;
        }
    }

    /// <summary>对防御塔的有效攻击力 = 蓝图攻击力 × attack 类状态乘算（weaken 在此生效）。</summary>
    public double EffectiveAttackPower
    {
        get
        {
            var mul = 1.0;
            foreach (var m in Modifiers)
                if (m.Stat == "attack")
                    mul *= m.Mul;
            return Blueprint.AttackPower * mul;
        }
    }

    /// <summary>受伤乘算：vulnerable 乘上去、resist 除下来（下限 0）。</summary>
    public double DamageTakenMultiplier
    {
        get
        {
            var mul = 1.0;
            foreach (var m in Modifiers)
            {
                if (m.Stat == "vulnerable")
                    mul *= m.Mul;
                else if (m.Stat == "resist")
                    mul /= m.Mul <= 0 ? 1 : m.Mul;
            }
            return Math.Max(0, mul);
        }
    }

    /// <summary>攻击落空概率 = blind 状态的 Mul（取最大值）。</summary>
    public double AttackMissChance
    {
        get
        {
            var chance = 0.0;
            foreach (var m in Modifiers)
                if (m.Stat == "blind")
                    chance = Math.Max(chance, Math.Clamp(m.Mul, 0, 1));
            return chance;
        }
    }

    /// <summary>每秒周期伤害 = dot 类状态的 Amount 求和（poison / burn 在此生效）。</summary>
    public double DoTDps
    {
        get
        {
            var dps = 0.0;
            foreach (var m in Modifiers)
                if (m.Stat == "dot")
                    dps += m.Amount;
            return dps;
        }
    }

    /// <summary>是否被标记（mark 元状态，无内建表现，供其他效果/事件引用）。</summary>
    public bool IsMarked => HasActive("mark");

    /// <summary>通用状态查询（含命名标记 mark_* 等 mod 自定义状态的读取口）。</summary>
    public bool HasStatus(string stat) => HasActive(stat);

    private bool HasActive(string stat) => Modifiers.Any(m => m.Stat == stat && m.Remaining > 0);

    /// <summary>距离终点还差多少格（越小越靠前，供 First 选敌）。</summary>
    public double DistToExit => Map.PathLength - PathDist;

    /// <summary>当前所站格。</summary>
    public CellPos Cell => Map.CellAt(PathDist);

    /// <summary>浮点位置（格间插值，供渲染）。</summary>
    public (double X, double Y) WorldPos => Map.PosAt(PathDist);
}
