namespace TowerGrid.Core;

/// <summary>
/// 效果注册表：效果种类（字符串 ID）→ 处理器。
/// 这是"易于增添 mod"的核心之一：新增一种效果只需 Register 一个处理器，
/// 之后就能在任意防御塔蓝图 / 技能的 EffectSpec.Kind 里引用它，无需改动仿真代码。
/// </summary>
public static class EffectRegistry
{
    /// <summary>效果处理器：在结算中把一条 EffectSpec 施加到目标上。</summary>
    public delegate void Handler(Simulation sim, Tower source, Entity target, EffectSpec spec);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal);

    /// <summary>注册/覆盖一个效果处理器（mod 入口）。</summary>
    public static void Register(string kind, Handler handler) => Handlers[kind] = handler;

    /// <summary>注销一个效果处理器（mod 卸载用）。</summary>
    public static bool Unregister(string kind) => Handlers.Remove(kind);

    public static bool Has(string kind) => Handlers.ContainsKey(kind);

    /// <summary>把一条效果结算到目标上；未知种类则记录日志（不抛异常，避免 mod 拼错 ID 直接炸掉仿真）。</summary>
    public static void Apply(Simulation sim, Tower source, Entity target, EffectSpec spec)
    {
        if (Handlers.TryGetValue(spec.Kind, out var handler))
            handler(sim, source, target, spec);
        else
            sim.PushLog($"[mod] 未注册的效果种类 “{spec.Kind}”（可用 EffectRegistry.Register(\"{spec.Kind}\", ...) 添加）");
    }

    static EffectRegistry() => RegisterBuiltins();

    private static void RegisterBuiltins()
    {
        // 伤害：UseSourceAttack = true 时伤害 = 攻击力 × Amount；强力击（NextAttackMultiplier）在此生效
        Register("damage", (sim, src, target, spec) =>
        {
            var amount = spec.UseSourceAttack ? src.EffectiveAttack * spec.Amount : spec.Amount;
            amount *= src.NextAttackMultiplier;
            if (target is Enemy e)
                sim.DamageEnemy(e, amount, src, spec.DamageType);
            else if (target is Tower t)
                sim.DamageTower(t, amount);
        });

        // 治疗：攻击特效之一，作用对象通常是防御塔（恢复生命）；打到敌人身上规则也不禁止 —— 设计自由。
        // 与 damage / grant_sp 同构：UseSourceAttack = true 时回复量 = 攻击力 × Amount（基础能力的百分比转换）。
        Register("heal", (sim, src, target, spec) =>
        {
            var amount = spec.UseSourceAttack ? src.EffectiveAttack * spec.Amount : spec.Amount;
            switch (target)
            {
                case Tower t:
                    sim.HealTower(t, amount);
                    break;
                case Enemy e:
                    sim.HealEnemy(e, amount);
                    break;
            }
        });

        // 充能：给友方塔提供技力；SpScaleByAttack = true 时提供量 = 攻击力 × Amount。
        // 多技能塔：Extra["skill"] 指定技能序号（默认 0 = 第一个技能），Extra["all"]=1 给全部技能充能。
        Register("grant_sp", (sim, src, target, spec) =>
        {
            if (target is not Tower t || t.Skills.Count == 0)
                return;
            var amount = spec.ExtraValue("flat", double.NaN);
            if (double.IsNaN(amount))
                amount = spec.SpScaleByAttack ? src.EffectiveAttack * spec.Amount : spec.Amount;
            if (spec.ExtraValue("all", 0) == 1)
            {
                foreach (var s in t.Skills)
                    s.GainSp(amount);
                return;
            }
            var idx = (int)spec.ExtraValue("skill", 0);
            if (idx >= 0 && idx < t.Skills.Count)
                t.Skills[idx].GainSp(amount);
        });

        // 属性增益：给友方塔挂 buff（attack / attack_speed，持续 Duration 秒）。
        // Permanent = true → 永久规则（不过期）；Condition（如 hp_below:0.4）→ 只在条件满足期间生效。
        Register("buff", (sim, _, target, spec) =>
        {
            if (target is Tower t && spec.Stat is { } stat)
                t.Buffs.Add(new Buff
                {
                    Stat = stat, Add = spec.Add, Mul = spec.Mul,
                    Remaining = spec.Permanent ? double.PositiveInfinity : spec.Duration,
                    Condition = spec.Condition, Permanent = spec.Permanent,
                });
        });

        // 击退：基础能力的一部分——与攻击力无关，距离为事先填入的格数（Amount）。
        // 设计上配弹幕型投递使用（命中实体时把敌人沿路径推回）。
        Register("knockback", (sim, src, target, spec) =>
        {
            if (target is Enemy e)
                sim.Knockback(e, spec.Amount, src as Tower);
        });

        // 事件盒：给目标挂一个"感知事件的盒子"——监听 TriggerEvent（主语=携带者；null = 定时盒，
        // Duration 秒后开盒）。开盒时以携带者为锚对 Payload 区域结算装填效果（效果可任意组合）。
        Register("trigger_box", (sim, src, target, spec) =>
        {
            if (spec.Payload is not { } payload)
                return;
            var box = new TrapBox
            {
                Carrier = target, Source = src as Tower ?? throw new InvalidOperationException("trigger_box 的来源必须是塔"),
                Payload = payload, TriggerEvent = spec.TriggerEvent,
                Remaining = spec.TriggerEvent is null ? spec.Duration : spec.Duration <= 0 ? 0 : spec.Duration,
            };
            target.TrapBoxes.Add(box);
            if (src is Tower tw)
                sim.PushLog($"{tw.Blueprint.Name} 给 {Simulation.NameOf(target)} 挂上事件盒（{(spec.TriggerEvent is null ? $"{spec.Duration}秒后开启" : $"监听 {spec.TriggerEvent}")}）");
        });

        // ------------------------------------------------------------------
        // 敌人状态类：效果只负责"把状态写到目标身上"，含义由 Enemy 的解释点定义。
        // 同状态重复施加 = 刷新时长（取更长的），强度取后一次施加的值。
        // ------------------------------------------------------------------

        // 眩晕：完全失去行动（不动不攻击）
        Register("stun", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "stun", spec.Duration);
        });

        // 束缚：不可移动，仍可攻击
        Register("bind", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "bind", spec.Duration);
        });

        // 沉默：禁用特殊能力（敌人侧解释点为后续扩展预留）
        Register("silence", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "silence", spec.Duration);
        });

        // 减速：移速 × Mul（如 0.6 = 减速 40%）
        Register("slow", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "speed", spec.Duration, mul: spec.Mul);
        });

        // 虚弱：对塔攻击力 × Mul
        Register("weaken", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "attack", spec.Duration, mul: spec.Mul);
        });

        // 易伤：受到全部伤害 × Mul（Arknights 式 Fragile）
        Register("vulnerable", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "vulnerable", spec.Duration, mul: spec.Mul);
        });

        // 减伤：受到伤害 ÷ Mul
        Register("resist", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "resist", spec.Duration, mul: spec.Mul);
        });

        // 中毒 / 灼烧：同一解释点（dot），每秒 Amount 点周期伤害
        Register("poison", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "dot", spec.Duration, amount: spec.Amount);
        });
        Register("burn", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "dot", spec.Duration, amount: spec.Amount);
        });

        // 致盲：攻击按 Mul（落空概率 0~1）可能落空
        Register("blind", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "blind", spec.Duration, mul: spec.Mul);
        });

        // 标记：元状态，无内建表现；其他效果/事件可经 Enemy.IsMarked / HasStatus("mark") 引用
        Register("mark", (sim, _, target, spec) =>
        {
            if (target is Enemy e)
                sim.ApplyEnemyStatus(e, "mark", spec.Duration);
        });

        // 强力击：使自己的下次普攻伤害 ×mult（叠加为乘算），普攻结算后消耗
        Register("empower_next_attack", (sim, src, _, spec) =>
            src.NextAttackMultiplier *= spec.ExtraValue("mult", 2));

        // 连击：使自己的下次普攻执行 count 次，普攻结算后清零
        Register("multicast_next_attack", (sim, src, _, spec) =>
            src.MulticastStacks += (int)spec.ExtraValue("count", 2));

        // 射程档位：把目标塔切到指定档（Extra["tier"]，省略 = 升一档；越界取边界）。
        // 目标塔没有档位表则无效果 —— "升级友方塔"作为塔↔塔能力 / 技能效果的表达。
        Register("set_range_tier", (sim, _, target, spec) =>
        {
            if (target is Tower t && t.Blueprint.RangeTiers is { Count: > 0 } tiers)
            {
                var tier = Math.Clamp((int)spec.ExtraValue("tier", t.RangeTier + 1), 0, tiers.Count - 1);
                if (tier != t.RangeTier)
                {
                    t.RangeTier = tier;
                    sim.PushLog($"{t.Name} 射程档位 → {tier + 1}/{tiers.Count}");
                }
            }
        });
    }
}
