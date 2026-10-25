namespace TowerGrid.Core;

/// <summary>
/// 攻击范围：一组以"局部坐标"描述的格子偏移。
/// 局部坐标 (F, S)：F = 沿朝向前方的格距（可为负 = 身后），S = 相对朝向的侧向偏移（+ 为朝向右手侧）。
/// 世界格 = 按防御塔朝向旋转后平移到塔身位置 —— 因此同一份范围数据可用于任意朝向，
/// 范围形状天然就是"由若干正方形格子组成、一般偏向朝向一侧"。
/// </summary>
public sealed record RangeShape(IReadOnlyList<(int F, int S)> Cells)
{
    public static readonly RangeShape Empty = new([]);

    /// <summary>手工指定偏移格。</summary>
    public static RangeShape Of(params (int F, int S)[] cells) => new(cells);

    /// <summary>仅自身所在格（给自身施加效果时使用）。</summary>
    public static RangeShape Self => Of((0, 0));

    /// <summary>朝向前方 1..forward 格的直线（宽 1）。</summary>
    public static RangeShape Line(int forward) => Rect(1, forward, 0);

    /// <summary>
    /// 朝向前方 forwardMin..forwardMax 格、横向 ±halfWidth 的矩形。
    /// forwardMin &gt; 1 即可实现"只能打远处、打不了近处"的最小射程（迫击炮类）。
    /// </summary>
    public static RangeShape Rect(int forwardMin, int forwardMax, int halfWidth)
    {
        var cells = new List<(int F, int S)>();
        for (var f = forwardMin; f <= forwardMax; f++)
            for (var s = -halfWidth; s <= halfWidth; s++)
                cells.Add((f, s));
        return new RangeShape(cells);
    }

    /// <summary>以自身为中心、四向半径 radius 内的方形邻域（旋转无关，不含自身格）。</summary>
    public static RangeShape Around(int radius)
    {
        var cells = new List<(int F, int S)>();
        for (var f = -radius; f <= radius; f++)
            for (var s = -radius; s <= radius; s++)
                if (f != 0 || s != 0)
                    cells.Add((f, s));
        return new RangeShape(cells);
    }

    /// <summary>把局部偏移按朝向旋转并平移，得到世界格子列表（已去重）。</summary>
    public List<CellPos> ToWorld(CellPos origin, Direction facing)
    {
        var result = new List<CellPos>();
        var seen = new HashSet<CellPos>();
        foreach (var (f, s) in Cells)
        {
            // 前 = 朝向向量；右 = 朝向顺时针旋转 90°（屏幕坐标系）
            CellPos offset = facing switch
            {
                Direction.Up => new CellPos(s, -f),
                Direction.Down => new CellPos(-s, f),
                Direction.Left => new CellPos(-f, -s),
                _ => new CellPos(f, s),
            };
            var world = origin + offset;
            if (seen.Add(world))
                result.Add(world);
        }
        return result;
    }

    public HashSet<CellPos> ToWorldSet(CellPos origin, Direction facing) => new(ToWorld(origin, facing));
}

/// <summary>攻击对象阵营：敌方 / 友方 / 自身。防御塔"攻击"防御塔即 Friendly 对象。</summary>
public enum TargetSide
{
    Enemy,
    Friendly,
    Self,
}

/// <summary>
/// 伤害类型：目标游戏的伤害属性三分（物理 / 法术 / 真实）。
/// 类型只是随伤害流动的**标签**：具体减免算法装填在 Simulation.Mitigate 缺口
/// （默认装填物读 Defense/SpellResist 做物理扣减/法术乘区/真实无视，之后叠乘状态乘区）——
/// 算法不是本脚本的任务，可整体替换成目标游戏自己的公式。
/// </summary>
public enum DamageType
{
    /// <summary>物理：先减目标 Defense（下限为原伤害 5%），是默认类型。</summary>
    Physical,

    /// <summary>法术：乘以 (1 - SpellResist)，不被 Defense 影响。</summary>
    Spell,

    /// <summary>真实：无视 Defense 与 SpellResist，只吃状态乘区（vulnerable/resist）。</summary>
    True,
}

/// <summary>目标挑选策略。</summary>
public enum TargetPick
{
    /// <summary>沿路径最靠前（最先抵达终点）的敌人；对友方则取最近的塔。</summary>
    First,

    /// <summary>生命值最高。</summary>
    Strongest,

    /// <summary>生命值最低。</summary>
    Weakest,

    /// <summary>范围内全部目标（受 MaxCount 约束）。</summary>
    All,

    /// <summary>外部指定的那个目标（由玩家/程序设置 Tower.DesignatedTarget），无效时回退最近目标。</summary>
    Designated,
}

/// <summary>
/// 目标选取描述：攻击谁（阵营）+ 在哪些格里找（范围）+ 挑哪几个（策略与数量）。
/// Shape 为 null 时表示"跟随该塔当前生效的攻击范围"（含射程档位）——
/// 有档位的塔建议省略 Shape，升级换档时攻击范围自动跟着变。
/// MaxCount 对任意 Pick 策略生效 = **单次攻击最多选取的目标数**（多目标索敌）：
/// 显式指定 N 即"锁 N 个目标，各结算一次"；省略（∞ 哨兵）时 All=全部、其他策略=1（单目标）。
/// "同目标多发"用 multicast_next_attack 表达，与多目标索敌正交、可组合（N 目标 × M 连发）。
/// </summary>
public sealed record TargetSpec(TargetSide Side, RangeShape? Shape = null, TargetPick Pick = TargetPick.First, int MaxCount = int.MaxValue);

/// <summary>
/// 一条效果描述（纯数据）。效果的实际行为由 EffectRegistry 中注册的同名处理器执行 ——
/// 内置 18 种：damage / heal / grant_sp / buff / 状态类（stun / bind / slow / poison / burn …）/
/// empower_next_attack / multicast_next_attack / set_range_tier。
/// mod 新增效果 = 注册一个新 Kind，然后在任意蓝图里引用它。
/// </summary>
public sealed record EffectSpec
{
    /// <summary>效果种类（EffectRegistry 中注册的 ID）。</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 通用数量。damage/heal：为伤害/治疗量；UseSourceAttack=true 时表示"攻击力 × Amount"。
    /// grant_sp：为技力量；SpScaleByAttack=true 时表示"攻击力 × Amount"。
    /// </summary>
    public double Amount { get; init; } = 1;

    /// <summary>持续时长（秒）：buff / 眩晕类效果使用。</summary>
    public double Duration { get; init; }

    /// <summary>damage 时：伤害是否取"攻击力 × Amount"（为 true 则 Amount 是攻击力倍率）。</summary>
    public bool UseSourceAttack { get; init; }

    /// <summary>grant_sp 时：提供量是否取"攻击力 × Amount"。</summary>
    public bool SpScaleByAttack { get; init; }

    /// <summary>buff 目标属性：attack（攻击力）/ attack_speed（攻速，Mul 越大越快）。</summary>
    public string? Stat { get; init; }

    /// <summary>伤害类型（damage 效果使用，默认物理）。</summary>
    public DamageType DamageType { get; init; } = DamageType.Physical;

    /// <summary>
    /// 条件 ID（buff 效果使用，可选）：挂上后本条增益只在条件满足期间生效（连续求值，随状态开关）。
    /// 内置 hp_below:P / hp_above:P（P 为比例 0~1）；mod 可经 ConditionRegistry 注册自定义条件。
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>true = 永久增益（永不过期；配合 Condition 即"条件型属性规则"）。</summary>
    public bool Permanent { get; init; }

    /// <summary>buff 数值：Add 加算（+X 攻击力），Mul 乘算（1.2 = +20%）。</summary>
    public double Add { get; init; }

    public double Mul { get; init; } = 1;

    /// <summary>mod 自定义扩展参数（ empower_next_attack 的 "mult"、multicast_next_attack 的 "count" 等）。</summary>
    public IReadOnlyDictionary<string, double>? Extra { get; init; }

    public double ExtraValue(string key, double fallback = 0) =>
        Extra is not null && Extra.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>
    /// 效果传导：主效果结算后，其衰减副本在目标周围指定范围内传递次级效果（共 Chain.Times 次）。
    /// 可挂任意效果上（damage / heal / poison …传导的都是该效果的衰减副本）。
    /// </summary>
    public ChainSpec? Chain { get; init; }

    /// <summary>
    /// 事件盒（kind="trigger_box"）：盒子挂到目标身上后监听的事件名（主语=携带者，如 enemy.death）。
    /// null = 定时盒：Duration 秒后自动开盒。开盒时以携带者为锚对 Payload 区域结算装填效果。
    /// </summary>
    public string? TriggerEvent { get; init; }

    /// <summary>事件盒装填的效果（完整 ActionSpec：目标描述 + 效果列表，锚点 = 携带者位置）。</summary>
    public ActionSpec? Payload { get; init; }
}

/// <summary>
/// 效果传导描述：主效果的某部分以每次传递削弱一定百分比的形式，在对应目标周围指定范围内
/// 传递次级效果，共传递 Times 次。是否允许同一目标被重复传递由 AllowRepeat 决定。
/// </summary>
public sealed record ChainSpec
{
    /// <summary>传递搜索范围：以当前携带者为锚的范围形状（推荐 Around(k)）。</summary>
    public required RangeShape Radius { get; init; }

    /// <summary>每次传递的衰减乘数（如 0.6 = 每传一次效果量 ×0.6）。</summary>
    public double Falloff { get; init; } = 1;

    /// <summary>传递次数（链条长度）。</summary>
    public int Times { get; init; } = 1;

    /// <summary>false（默认）= 已被链条命中过的目标不再接收（经典闪电链）；true = 允许重复命中。</summary>
    public bool AllowRepeat { get; init; }
}

/// <summary>
/// 投递方式：攻击效果如何到达作用对象，分三型 ——
/// 弹幕型（发射弹幕，存在飞行时间，才有指向性/目标性与追踪之说）、
/// 射线型（发射即沿射线路径作用全部指定类型单位，无飞行时间、无追踪）、
/// 召唤型（不从塔位发射：发射时锁定落区，延迟后区域结算，无弹道无追踪）。
/// 省略 Delivery = 无投递（瞬发）：出手瞬间直接在锁定目标身上结算。
/// 所有型都发生在攻击间隔之后 —— 间隔只决定何时出手，型决定出手后效果如何到达/何时生效。
/// </summary>
public enum DeliveryKind
{
    /// <summary>无投递（默认，省略 Delivery 即此）：出手瞬间直接在锁定目标身上结算。</summary>
    Instant,

    /// <summary>弹幕型·指向性·死守：弹道只认发射时锁定的目标，全程朝它前进；目标死亡 → 弹道立即消失（不转移、不落地）。</summary>
    Homing,

    /// <summary>弹幕型·指向性·择新：原定目标死亡时按原索敌规则（TargetSpec）自动转追其他敌人；无新目标才消散。</summary>
    HomingRetarget,

    /// <summary>弹幕型·目标性：发射时锁定落点，弹道沿发射向量直线飞行、不跟随目标移动，到达落点时结算。</summary>
    Directional,

    /// <summary>
    /// 射线型：发射射线，从正式发射开始即直接作用射线路径上的**所有指定类型单位**——
    /// 无飞行时间限制、无弹幕追踪。TargetSpec.Shape 即射线路径（应为直线，如 Line(N)），Side 为类型过滤。
    /// </summary>
    Beam,

    /// <summary>
    /// 召唤型：不从塔位发射，以"召唤"形式直接在锁定落区生效（无弹道，不存在指向性/目标性/追踪）。
    /// 发射时锁定落区（定区 Area / 定点=锁定目标的发射时位置），经 Delay 秒后出现并按作用区间区域结算；
    /// 期间目标移动不影响落区（不跟随）。
    /// </summary>
    Summon,
}

/// <summary>
/// 投递描述（可选，挂在 ActionSpec 上）。
/// 语义链：TargetSpec 决定"朝谁发射"（锁定目标 / 确定落点）→ Delivery 决定"怎么到达、何时生效"
/// → Impact 决定"生效时以哪个位置区域为作用区间选取结算对象"（null = 仅命中目标 / 落点单格）。
/// </summary>
public sealed record DeliverySpec
{
    public required DeliveryKind Kind { get; init; }

    /// <summary>弹道速度（格/秒）。</summary>
    public double Speed { get; init; } = 8;

    /// <summary>
    /// 作用区间：效果生效的位置区域（以命中点/落点为锚、Source 朝向为旋转基准）。
    /// 生效时在区间内**当时在场**的实体中按 TargetSpec.Side/Pick 重新选取结算对象——
    /// 这就是"目标性不跟随"的落点：目标离开区间就炸空。
    /// null = 指向性（死守/择新）直接作用于跟踪目标本身；目标性则退化为落点单格；
    /// 召唤型·定点 = 每个锚点格单爆。
    /// </summary>
    public RangeShape? Impact { get; init; }

    /// <summary>召唤型：'召唤→出现'的间隔秒数（攻击间隔只决定何时召唤；本字段决定效果何时落地）。</summary>
    public double Delay { get; init; } = 1;

    /// <summary>
    /// 召唤型·定区落区：以塔为基准的固定区域（随朝向旋转），召唤效果在该区域整体落地（此时忽略 Impact）。
    /// null = 定点模式：锚定每个锁定目标的**发射时所在格**（不跟随目标移动），各配 Impact 区间落地。
    /// </summary>
    public RangeShape? Area { get; init; }
}

/// <summary>一次"攻击" = 攻击对象（TargetSpec）+ 一串效果（EffectSpec）+ 投递方式（DeliverySpec?，省略=瞬发）。普通攻击与技能都由它表达，规则完全统一。</summary>
public sealed record ActionSpec(TargetSpec Target, IReadOnlyList<EffectSpec> Effects, DeliverySpec? Delivery = null);

public enum SkillTriggerMode
{
    /// <summary>技力满后自动释放。</summary>
    Auto,

    /// <summary>技力满后等待手动触发（玩家点击 / 程序调用 Simulation.TriggerSkill）。</summary>
    Manual,
}

/// <summary>
/// 技力条 + 特殊技能。技力是浮点值，来源 = 自然恢复（SpRegenPerSecond）+ 事件回复（SpOnEvent）；
/// 攒满后按 Trigger 模式释放 Action。它是特殊技能触发的前置条件。
/// </summary>
public sealed record SkillSpec
{
    /// <summary>技能名（展示用，可带说明）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>技力上限 n（浮点）。</summary>
    public required double SpMax { get; init; }

    /// <summary>部署时自带的初始技力。</summary>
    public double InitialSp { get; init; }

    /// <summary>每秒自然恢复的技力。</summary>
    public double SpRegenPerSecond { get; init; }

    /// <summary>
    /// 事件回复表：事件名 → 每次获得的技力。事件名可用 GameEvents 常量，也可用 mod 自定义事件名；
    /// 约定：事件的主语塔（SimEvent.Tower）获得技力。例：["tower.attack"]=1 表示每次普攻 +1。
    /// </summary>
    public IReadOnlyDictionary<string, double> SpOnEvent { get; init; } = new Dictionary<string, double>();

    public SkillTriggerMode Trigger { get; init; } = SkillTriggerMode.Auto;

    /// <summary>触发后进入"技能模式"的持续秒数。仅 ReplaceNormalAttack = true 时使用：期间普通攻击被本技能的 Action 取代。</summary>
    public double Duration { get; init; }

    /// <summary>true = 触发后 Duration 秒内普通攻击被本技能的 Action 取代（明日方舟式"技能期替换攻击"，不立即结算）；false = 触发时立即结算一次。</summary>
    public bool ReplaceNormalAttack { get; init; }

    /// <summary>技能内容（一次攻击：对象 + 效果）。替换模式下按普攻节奏反复结算。</summary>
    public required ActionSpec Action { get; init; }
}

/// <summary>事件触发型被动："被动 = 以事件为扳机的攻击"。主语塔监听的事件发生时，按概率结算一次 Action。</summary>
public sealed record PassiveSpec
{
    /// <summary>监听的事件名（GameEvents 常量或 mod 自定义事件）。</summary>
    public required string EventName { get; init; }

    /// <summary>事件触发时执行的攻击（对象 + 效果）。</summary>
    public required ActionSpec Action { get; init; }

    /// <summary>触发概率 [0,1]，默认必发。</summary>
    public double Chance { get; init; } = 1;

    /// <summary>展示名（可选，如“反击”“击杀回响”）。</summary>
    public string DisplayName { get; init; } = "";
}

/// <summary>防御塔蓝图（静态定义数据，运行时实例见 Tower）。新塔 = new 一份蓝图并注册进 TowerLibrary。</summary>
public sealed record TowerBlueprint
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>一句话说明（演示 UI 展示）。</summary>
    public string Description { get; init; } = "";

    public required double MaxHp { get; init; }

    /// <summary>攻击力。普攻伤害（UseSourceAttack）与按攻击力结算的效果（如充能 SpScaleByAttack）都引用运行时有效攻击力。</summary>
    public required double AttackPower { get; init; }

    /// <summary>普攻间隔（秒）。</summary>
    public required double AttackInterval { get; init; }

    /// <summary>攻击范围形状（局部坐标，随朝向旋转）。作为第 0 档；定义 RangeTiers 后即档位体系的基准档。</summary>
    public required RangeShape AttackRange { get; init; }

    /// <summary>
    /// 射程档位表（可选）：预设的若干档范围形状，运行时经 Tower.RangeTier 切换（升级 / 效果 / 程序设置）。
    /// 按当初取舍不做任意形状动态缩放（会变形），档位即"升级换形状"的表达方式。
    /// </summary>
    public IReadOnlyList<RangeShape>? RangeTiers { get; init; }

    /// <summary>普通攻击（攻击对象 + 效果列表）。</summary>
    public required ActionSpec NormalAttack { get; init; }

    /// <summary>
    /// 该塔携带的技能列表：一座塔可带多个技能，每个技能一条**独立技力条**、独立触发模式与运行时覆盖。
    /// 多个"替换普攻"技能同时生效时，后触发的覆盖先触发的（同一时间只有一个替换模式）。
    /// </summary>
    public IReadOnlyList<SkillSpec> Skills { get; init; } = [];

    /// <summary>事件触发型被动（"以事件为扳机的攻击"）。主语塔监听的事件发生时按概率自动结算。</summary>
    public IReadOnlyList<PassiveSpec> Passives { get; init; } = [];

    /// <summary>是否阻挡敌人通行（放在路径上时会挡路并成为敌人的攻击目标）。</summary>
    public bool BlocksPath { get; init; } = true;

    /// <summary>渲染色（演示层使用）。</summary>
    public string Color { get; init; } = "#4fb8a8";
}

/// <summary>敌人蓝图（静态定义数据）。</summary>
public sealed record EnemyBlueprint
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double MaxHp { get; init; }

    /// <summary>移动速度（格/秒）。</summary>
    public required double Speed { get; init; }

    /// <summary>被防御塔挡住去路时，对塔的攻击力。</summary>
    public double AttackPower { get; init; }

    /// <summary>对塔的攻击间隔（秒）。</summary>
    public double AttackInterval { get; init; } = 1;

    /// <summary>物理防御（.flat 扣减）：受到的物理伤害先减去此值（下限为原伤害 5%）。</summary>
    public double Defense { get; init; }

    /// <summary>法术抗性（0~1 比例）：受到的法术伤害 × (1 - 此值)。真实伤害无视两者。</summary>
    public double SpellResist { get; init; }

    /// <summary>渲染色（演示层使用）。</summary>
    public string Color { get; init; } = "#e05b4f";
}

/// <summary>一条刷怪安排：波次开始后 At 秒起，每 Interval 秒刷一个，共 Count 个。</summary>
public sealed record WaveSpawn(string EnemyId, double At, int Count = 1, double Interval = 1);

/// <summary>一个波次（若干条刷怪安排）。</summary>
public sealed record WaveDef(IReadOnlyList<WaveSpawn> Spawns);
