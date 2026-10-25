namespace TowerGrid.Core;

public enum GameState
{
    Running,
    GameOver,
}

/// <summary>
/// 战斗仿真主循环：固定步长 Step(dt) 推进，与渲染完全解耦、无任何引擎依赖 ——
/// 同一套仿真可以直接搬进 Unity/Godot/服务器。
/// 塔的普攻与技能统一为 ActionSpec（对象 + 效果）结算；技力条、事件、效果都走开放注册表，方便 mod 扩展。
/// </summary>
public sealed class Simulation
{
    private readonly IReadOnlyList<WaveDef> _waves;
    private readonly List<(double At, string EnemyId)> _pendingSpawns = [];
    private readonly List<string> _log = [];
    private int _waveIndex;
    private int _loop;
    private double _nextWaveTime;
    private int _nextId;

    public Simulation(GridMap map, IReadOnlyList<WaveDef> waves, int seed = 1, double firstWaveDelay = 3)
    {
        Map = map;
        _waves = waves;
        Rng = new Random(seed);
        _nextWaveTime = firstWaveDelay;
        _ = EffectRegistry.Has("damage"); // 触发内置效果注册
    }

    public GridMap Map { get; }
    public double Time { get; private set; }

    /// <summary>基地生命：敌人抵达终点时 -1，归零即失败。</summary>
    public int BaseHp { get; set; } = 10;
    public GameState State { get; private set; } = GameState.Running;

    public List<Tower> Towers { get; } = [];
    public List<Enemy> Enemies { get; } = [];
    public EventBus Events { get; } = new();
    public Random Rng { get; }

    /// <summary>当前波次编号（含循环轮数）。</summary>
    public int WaveNumber { get; private set; }

    /// <summary>循环轮数带来的敌人成长（每轮 ×1.25 生命）。</summary>
    public double EnemyHpScale => Math.Pow(1.25, _loop);

    public IReadOnlyList<string> Log => _log;

    public void PushLog(string msg)
    {
        _log.Add($"[{Time,6:0.0}s] {msg}");
        if (_log.Count > 80)
            _log.RemoveAt(0);
    }

    // ---------------------------------------------------------------- 建造

    public Tower? PlaceTower(TowerBlueprint bp, CellPos cell, Direction facing)
    {
        if (State != GameState.Running || !Map.InBounds(cell) || IsOccupied(cell))
        {
            PushLog($"{bp.Name} 无法部署于 {cell}（{(State != GameState.Running ? "非运行状态" : !Map.InBounds(cell) ? "越界" : "格子被占用")}）");
            return null;
        }
        var t = new Tower
        {
            InstanceId = $"T{_nextId++}",
            Blueprint = bp,
            Cell = cell,
            Facing = facing,
            Hp = bp.MaxHp,
        };
        foreach (var spec in bp.Skills)
            t.Skills.Add(new TowerSkill { Spec = spec, Sp = spec.InitialSp });
        Towers.Add(t);
        Publish(new SimEvent { Name = GameEvents.TowerPlaced, Tower = t, Cell = cell });
        PushLog($"{bp.Name} 部署于 {cell}，朝向 {facing}");
        return t;
    }

    public void RemoveTower(Tower t)
    {
        if (!Towers.Remove(t))
            return;
        foreach (var other in Towers)
            if (other.DesignatedTarget == t)
                other.DesignatedTarget = null;
        if (SelectedTower == t)
            SelectedTower = null;
        Publish(new SimEvent { Name = GameEvents.TowerRemoved, Tower = t, Cell = t.Cell });
        PushLog($"{t.Name} 已拆除");
    }

    public bool IsOccupied(CellPos cell) => Towers.Any(t => t.Alive && t.Cell == cell);

    public Tower? TowerAt(CellPos cell) => Towers.FirstOrDefault(t => t.Alive && t.Cell == cell);

    /// <summary>演示 UI 当前选中的塔（纯 UI 状态，不参与逻辑，放这里仅为方便单页面持有）。</summary>
    public Tower? SelectedTower { get; set; }

    // ---------------------------------------------------------------- 主循环

    public void Step(double dt)
    {
        if (State != GameState.Running)
            return;
        Time += dt;
        UpdateWaves();
        SpawnPending();
        UpdateEnemies(dt);
        UpdateTowers(dt);
        UpdateProjectiles(dt);
        UpdateSummons(dt);
        UpdateTrapBoxes(dt);
        Publish(new SimEvent { Name = GameEvents.Tick });
    }

    // ---------------------------------------------------------------- 波次

    private void UpdateWaves()
    {
        if (Time < _nextWaveTime)
            return;
        if (_waveIndex >= _waves.Count)
        {
            _waveIndex = 0;
            _loop++;
            PushLog($"—— 敌潮增强（第 {_loop + 1} 轮，生命 ×{EnemyHpScale:0.##}）——");
        }
        var wave = _waves[_waveIndex++];
        var waveEnd = 0.0;
        foreach (var s in wave.Spawns)
        {
            waveEnd = Math.Max(waveEnd, s.At + s.Count * s.Interval);
            for (var i = 0; i < s.Count; i++)
                _pendingSpawns.Add((Time + s.At + i * s.Interval, s.EnemyId));
        }
        _nextWaveTime = Time + waveEnd + 3.5; // 波间喘息
        WaveNumber++;
        PushLog($"第 {WaveNumber} 波来袭！");
    }

    private void SpawnPending()
    {
        for (var i = _pendingSpawns.Count - 1; i >= 0; i--)
        {
            var (at, id) = _pendingSpawns[i];
            if (at <= Time)
            {
                _pendingSpawns.RemoveAt(i);
                SpawnEnemy(id);
            }
        }
    }

    public Enemy SpawnEnemy(string enemyId)
    {
        var bp = EnemyLibrary.Get(enemyId);
        var e = new Enemy
        {
            InstanceId = $"E{_nextId++}",
            Blueprint = bp,
            Map = Map,
            Hp = bp.MaxHp * EnemyHpScale,
        };
        Enemies.Add(e);
        Publish(new SimEvent { Name = GameEvents.EnemySpawn, Enemy = e, Cell = e.Cell });
        return e;
    }

    // ---------------------------------------------------------------- 敌人

    private void UpdateEnemies(double dt)
    {
        // 先清“停驻”标记，再按路程从前往后处理：前面的敌人先行动，后面的据此排队
        foreach (var e in Enemies)
            e.StoppedThisTick = false;

        foreach (var e in Enemies.OrderByDescending(x => x.PathDist).ToList()) // 快照：敌人可能在结算中被移除
        {
            if (!e.Alive)
                continue;

            // 状态到期清理（外部注入的状态统一在这里倒数）
            for (var i = e.Modifiers.Count - 1; i >= 0; i--)
            {
                e.Modifiers[i].Remaining -= dt;
                if (e.Modifiers[i].Remaining <= 0)
                    e.Modifiers.RemoveAt(i);
            }

            // 周期伤害（DoT）：固定 1 秒节拍结算一次；毒杀无击杀者归属
            e.DoTTimer -= dt;
            if (e.DoTTimer <= 0)
            {
                e.DoTTimer += 1;
                var dps = e.DoTDps;
                if (dps > 0)
                    DamageEnemy(e, dps, null, DamageType.True); // DoT 是状态解释点结算的持续伤害：按真实类型（只吃易伤/抗性乘区）
            }
            if (!e.Alive)
                continue;

            // 行动门：眩晕时不动不攻击（束缚的敌人仍会走到下面的攻击分支）
            if (e.IsStunned)
            {
                e.StoppedThisTick = true;
                continue;
            }

            // 下一格有阻挡塔 → 停下攻击它，直到摧毁
            var nextIdx = Math.Min(Map.PathCells.Count - 1, (int)Math.Floor(e.PathDist) + 1);
            var nextCell = Map.PathCells[nextIdx];
            var blocker = Map.PathSet.Contains(nextCell) ? TowerAt(nextCell) : null;
            if (blocker is { Blueprint.BlocksPath: true })
            {
                e.StoppedThisTick = true;
                e.AttackCooldown -= dt;
                if (e.AttackCooldown <= 0)
                {
                    e.AttackCooldown = e.Blueprint.AttackInterval;
                    // 致盲：本次攻击按概率落空
                    if (Rng.NextDouble() >= e.AttackMissChance)
                        DamageTower(blocker, e.EffectiveAttackPower, e);
                }
                continue;
            }

            // 束缚：可攻击（上方已处理）但不可移动
            if (e.IsBound)
            {
                e.StoppedThisTick = true;
                continue;
            }

            // 前方格被停驻的敌人占着 → 排队等待（在塔后形成队列，而不是全部叠上去同时攻击）
            var queued = Enemies.Any(o => o != e && o.Alive && o.StoppedThisTick && o.Cell == nextCell);
            if (queued)
            {
                e.StoppedThisTick = true;
                continue;
            }

            e.PathDist = Math.Min(Map.PathLength, e.PathDist + e.EffectiveSpeed * dt);
            if (e.PathDist >= Map.PathLength)
                LeakEnemy(e);
        }
    }

    private void LeakEnemy(Enemy e)
    {
        Enemies.Remove(e);
        BaseHp--;
        Publish(new SimEvent { Name = GameEvents.EnemyLeak, Enemy = e, Cell = Map.ExitCell, Amount = 1 });
        if (BaseHp <= 0)
        {
            BaseHp = 0;
            State = GameState.GameOver;
            PushLog("基地沦陷 —— 游戏结束（可点“重新开始”）");
        }
        else
        {
            PushLog($"{e.Blueprint.Name} 突破防线！基地 -1（剩 {BaseHp}）");
        }
    }

    // ---------------------------------------------------------------- 塔：索敌 / 攻击 / 技能

    private void UpdateTowers(double dt)
    {
        foreach (var t in Towers.ToList())
        {
            if (!t.Alive)
                continue;

            // 技能：每条技力条独立恢复；自动型攒满即放
            foreach (var sk in t.Skills)
            {
                if (sk.Spec.SpRegenPerSecond > 0)
                    sk.GainSp(sk.Spec.SpRegenPerSecond * dt);
                if (sk.EffectiveTrigger == SkillTriggerMode.Auto && sk.Ready)
                    TriggerSkill(t, sk, null);
            }

            // buff 到期
            for (var i = t.Buffs.Count - 1; i >= 0; i--)
            {
                t.Buffs[i].Remaining -= dt;
                if (t.Buffs[i].Remaining <= 0)
                    t.Buffs.RemoveAt(i);
            }

            // 替换普攻模式：同一时间只有一个生效（后触发覆盖先触发），结束时清引用
            if (t.ActiveReplaceSkill is { ModeRemaining: <= 0 })
                t.ActiveReplaceSkill = null;
            if (t.ActiveReplaceSkill is { } active)
                active.ModeRemaining -= dt;

            // 出手：替换模式激活时用该技能的 Action，否则普攻（冷却好了且有目标才出手）
            if (t.AttackCooldown > 0)
                t.AttackCooldown -= dt;
            if (t.AttackCooldown <= 0)
            {
                var replaceMode = t.ActiveReplaceSkill is { ModeRemaining: > 0 };
                var action = replaceMode ? t.ActiveReplaceSkill!.Spec.Action : t.Blueprint.NormalAttack;
                var targets = FindTargets(t, action.Target);
                if (targets.Count > 0)
                {
                    PerformAction(t, action, targets, isSkill: replaceMode);
                    t.AttackCooldown = t.EffectiveInterval;
                }
            }

            // 手动技能等待 Simulation.TriggerSkill 调用（自动型已在上方处理）
        }
    }

    /// <summary>释放指定序号的技能（手动触发入口，序号默认 0 = 第一个技能；也可由 mod/程序直接调用）。</summary>
    public bool TriggerSkill(Tower t, int skillIndex = 0, IReadOnlyList<Entity>? explicitTargets = null)
    {
        if (skillIndex < 0 || skillIndex >= t.Skills.Count)
            return false;
        return TriggerSkill(t, t.Skills[skillIndex], explicitTargets);
    }

    /// <summary>
    /// 释放技能。技力条是技能的前置条件：不满则返回 false。
    /// <paramref name="explicitTargets"/>：程序显式提供的作用对象 —— 提供时不再按 TargetSpec 索敌、
    /// 也不做范围校验（调用方自己负责），只过滤已死亡目标；为 null 时按技能的 TargetSpec 自动索敌。
    /// </summary>
    private bool TriggerSkill(Tower t, TowerSkill skill, IReadOnlyList<Entity>? explicitTargets)
    {
        if (!skill.Ready || !t.Alive || State != GameState.Running)
            return false;
        skill.Sp = 0;
        Publish(new SimEvent { Name = GameEvents.SkillTriggered, Tower = t, Cell = t.Cell, Message = skill.Spec.DisplayName });
        PushLog($"{t.Name} 发动【{skill.Spec.DisplayName}】");

        // 替换模式：不立即结算，进入技能模式（多个替换技能后触发覆盖先触发）
        if (skill.Spec.ReplaceNormalAttack)
        {
            if (t.ActiveReplaceSkill is { } old && old != skill)
                old.ModeRemaining = 0;
            skill.ModeRemaining = skill.Spec.Duration;
            t.ActiveReplaceSkill = skill;
            return true;
        }

        var targets = explicitTargets is not null
            ? explicitTargets.Where(x => x.Alive).ToList()
            : FindTargets(t, skill.Spec.Action.Target);
        PerformAction(t, skill.Spec.Action, targets, isSkill: true);
        return true;
    }

    /// <summary>执行一次攻击：按投递方式决定"立即结算"还是"发出弹道延迟结算"。</summary>
    private void PerformAction(Tower src, ActionSpec action, List<Entity> targets, bool isSkill, bool viaProjectile = false)
    {
        // 召唤型：不从塔位发射——发射时锁定落区（定区 Area / 定点=锁定目标发射时位置），Delay 后区域结算
        if (!viaProjectile && action.Delivery is { Kind: DeliveryKind.Summon } summon && targets.Count > 0)
        {
            var snapshot = src.NextAttackMultiplier; // 普攻修饰符"发射即消耗"，倍率随召唤快照携带
            var casts = Math.Max(1, src.MulticastStacks);
            var anchors = summon.Area is { } area
                ? area.ToWorld(src.Cell, src.Facing).ToList() // 定区：塔为基准的固定落区（落地时忽略 Impact）
                : targets.Select(CellOf).ToList();            // 定点：锁定目标的发射时位置（不跟随目标移动）
            for (var c = 0; c < casts; c++)
                PendingSummons.Add(new PendingSummon
                {
                    InstanceId = $"summon-{_nextId++}", Source = src, Action = action, Delivery = summon,
                    IsSkill = isSkill, SnapshotMultiplier = snapshot, Anchors = anchors,
                    Facing = src.Facing, Remaining = summon.Delay,
                });
            if (!isSkill)
            {
                src.NextAttackMultiplier = 1;
                src.MulticastStacks = 0;
            }
            Publish(new SimEvent
            {
                Name = GameEvents.SummonCast, Tower = src, Cell = src.Cell,
                TargetCell = anchors[0], Data = isSkill ? "skill" : "normal",
            });
            return;
        }

        // 弹幕型：发射时锁定目标/落点，结算延迟到命中/落地（viaProjectile 弹道命中时不再二次发射）
        if (!viaProjectile && action.Delivery is { Kind: DeliveryKind.Homing or DeliveryKind.HomingRetarget or DeliveryKind.Directional } delivery && targets.Count > 0)
        {
            var snapshot = src.NextAttackMultiplier; // 普攻修饰符"发射即消耗"，倍率随弹道快照携带
            var casts = Math.Max(1, src.MulticastStacks);
            for (var c = 0; c < casts; c++)
                foreach (var t in targets)
                    LaunchProjectile(src, action, delivery, t, snapshot, isSkill);
            if (!isSkill)
            {
                src.NextAttackMultiplier = 1;
                src.MulticastStacks = 0;
            }
            Publish(new SimEvent
            {
                Name = GameEvents.ProjectileLaunch,
                Tower = src,
                Cell = src.Cell,
                TargetCell = CellOf(targets[0]),
                Data = isSkill ? "skill" : "normal",
            });
            return;
        }

        var casts2 = viaProjectile ? 1 : Math.Max(1, src.MulticastStacks); // 齐射已在发射时展开为多发弹道
        for (var c = 0; c < casts2; c++)
            foreach (var target in targets)
                foreach (var effect in action.Effects)
                {
                    EffectRegistry.Apply(this, src, target, effect);
                    if (effect.Chain is { } chain)
                        ChainEffect(src, target, effect, chain, action.Target.Side);
                }

        // 强力击 / 连击都在“普攻”结算后消耗（技能是它们的来源，不能自己吃掉自己）
        // 弹道命中的普攻结算不清——修饰符已在发射时消耗，命中值由 ResolveProjectile 注入/还原
        if (!isSkill && !viaProjectile)
        {
            src.NextAttackMultiplier = 1;
            src.MulticastStacks = 0;
        }

        var firstEnemy = targets.OfType<Enemy>().FirstOrDefault();
        var firstTower = targets.OfType<Tower>().FirstOrDefault();
        var isBeam = !viaProjectile && action.Delivery is { Kind: DeliveryKind.Beam };
        Publish(new SimEvent
        {
            Name = GameEvents.TowerAttack,
            Tower = src,
            Enemy = firstEnemy,
            TargetTower = firstTower,
            Cell = src.Cell,
            // 射线型：TargetCell = 射线路径末端格（视效画光束用）
            TargetCell = isBeam ? BeamEnd(src, action) : firstEnemy?.Cell ?? firstTower?.Cell ?? src.Cell,
            Data = viaProjectile ? "proj" : isBeam ? "beam" : isSkill ? "skill" : "normal",
        });
    }

    // ---------------------------------------------------------------- 弹道（投递方式 Homing / Directional 的运行时载体）

    /// <summary>场上所有在飞弹道（Web 校验器渲染弹道层直接遍历此列表）。</summary>
    public List<Projectile> Projectiles { get; } = [];

    /// <summary>场上所有待落地召唤（Web 校验器渲染落区预警层直接遍历此列表）。</summary>
    public List<PendingSummon> PendingSummons { get; } = [];

    private static CellPos CellOf(Entity e) => e switch
    {
        Enemy en => en.Cell,
        Tower t => t.Cell,
        _ => default,
    };

    /// <summary>实体显示名（日志用；供效果处理器等外部调用）。</summary>
    public static string NameOf(Entity e) => e switch
    {
        Enemy en => en.Blueprint.Name,
        Tower t => t.Blueprint.Name,
        _ => "目标",
    };

    /// <summary>实体视觉中心（格单位）：敌人为路径插值位置，塔为固定格中心。</summary>
    private static (double X, double Y) CenterOf(Entity e) => e switch
    {
        Enemy en => (en.WorldPos.X + 0.5, en.WorldPos.Y + 0.5),
        Tower t => (t.Cell.X + 0.5, t.Cell.Y + 0.5),
        _ => (0.5, 0.5),
    };

    private void LaunchProjectile(Tower src, ActionSpec action, DeliverySpec delivery, Entity target, double snapshot, bool isSkill)
    {
        var (sx, sy) = CenterOf(src);
        var impactCell = CellOf(target);
        Projectile p = delivery.Kind == DeliveryKind.Homing
            ? new Projectile
            {
                InstanceId = $"proj-{_nextId++}", Source = src, Action = action, Delivery = delivery,
                IsSkill = isSkill, SnapshotMultiplier = snapshot,
                HomingTarget = target, ImpactCell = impactCell, Facing = src.Facing,
                X = sx, Y = sy,
            }
            : new Projectile
            {
                InstanceId = $"proj-{_nextId++}", Source = src, Action = action, Delivery = delivery,
                IsSkill = isSkill, SnapshotMultiplier = snapshot,
                HomingTarget = null, ImpactCell = impactCell, Facing = src.Facing,
                X = sx, Y = sy,
            };
        if (delivery.Kind == DeliveryKind.Directional)
        {
            // 目标性：方向与落点在发射瞬间固定，此后不跟随目标移动
            var (tx, ty) = (impactCell.X + 0.5, impactCell.Y + 0.5);
            var dist = Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy));
            if (dist < 1e-9) dist = 1e-9;
            p.VX = (tx - sx) / dist * delivery.Speed;
            p.VY = (ty - sy) / dist * delivery.Speed;
        }
        Projectiles.Add(p);
    }

    private void UpdateProjectiles(double dt)
    {
        foreach (var p in Projectiles.ToList())
        {
            p.Age += dt;
            if (p.Age > 10) // 保险丝：追不上/不可达时消散
            {
                Projectiles.Remove(p);
                continue;
            }

            var step = p.Delivery.Speed * dt;
            if (p.Delivery.Kind is DeliveryKind.Homing or DeliveryKind.HomingRetarget)
            {
                // 指向性：跟踪目标当前位置。原定目标死亡后的行为按 死守/择新 二分。
                if (p.HomingTarget is not { Alive: true })
                {
                    if (p.Delivery.Kind == DeliveryKind.HomingRetarget)
                    {
                        // 择新型：按原索敌规则（TargetSpec）自动转追其他敌人；无新目标才消散。
                        var next = FindTargets(p.Source, p.Action.Target).FirstOrDefault();
                        if (next is null)
                        {
                            Projectiles.Remove(p);
                            PushLog($"{p.Source.Blueprint.Name} 的导弹失去目标且无可转追者——消散");
                            continue;
                        }
                        p.HomingTarget = next;
                        PushLog($"{p.Source.Blueprint.Name} 的导弹失去目标，转追 {NameOf(next)}");
                    }
                    else
                    {
                        // 死守型：只认发射时锁定的原定目标——目标死亡即弹道消失（不转移、不落地）。
                        Projectiles.Remove(p);
                        PushLog($"{p.Source.Blueprint.Name} 的弹道随原定目标消失——落空");
                        continue;
                    }
                }
                var (tx, ty) = CenterOf(p.HomingTarget!);
                var dx = tx - p.X; var dy = ty - p.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= step)
                {
                    ResolveProjectile(p, CellOf(p.HomingTarget!), p.Delivery.Impact is null ? p.HomingTarget : null);
                    continue;
                }
                p.VX = dx / dist * p.Delivery.Speed;
                p.VY = dy / dist * p.Delivery.Speed;
                p.X += p.VX * dt; p.Y += p.VY * dt;
            }
            else // Directional：沿发射向量直线飞行
            {
                var (tx, ty) = (p.ImpactCell.X + 0.5, p.ImpactCell.Y + 0.5);
                var dx = tx - p.X; var dy = ty - p.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= step)
                {
                    ResolveProjectile(p, p.ImpactCell, null);
                    continue;
                }
                p.X += p.VX * dt; p.Y += p.VY * dt;
            }
        }
    }

    /// <summary>弹道命中/落地的统一结算：注入发射时快照的普攻倍率 → 按投递方式结算 → 还原。</summary>
    private void ResolveProjectile(Projectile p, CellPos anchor, Entity? directTarget)
    {
        Projectiles.Remove(p);
        var saved = p.Source.NextAttackMultiplier;
        p.Source.NextAttackMultiplier = p.SnapshotMultiplier;
        try
        {
            if (directTarget is not null)
                PerformAction(p.Source, p.Action, [directTarget], p.IsSkill, viaProjectile: true);
            else
                DetonateZone(p.Source, p.Action, anchor, p.Facing, p.IsSkill);
        }
        finally { p.Source.NextAttackMultiplier = saved; }
    }

    // ---------------------------------------------------------------- 召唤（召唤型 Delivery 的延迟区域结算）

    private void UpdateSummons(double dt)
    {
        foreach (var s in PendingSummons.ToList())
        {
            s.Remaining -= dt;
            if (s.Remaining <= 0)
                ResolveSummon(s);
        }
    }

    /// <summary>召唤落地：注入发射时快照的普攻倍率 → 交给作用区间系统结算 → 还原。</summary>
    private void ResolveSummon(PendingSummon s)
    {
        PendingSummons.Remove(s);
        var saved = s.Source.NextAttackMultiplier;
        s.Source.NextAttackMultiplier = s.SnapshotMultiplier;
        try
        {
            if (s.Delivery.Area is not null)
                DetonateCells(s.Source, s.Action, s.Anchors, s.Anchors[0], s.IsSkill); // 定区：整片落区一次结算
            else
                foreach (var a in s.Anchors)
                    DetonateZone(s.Source, s.Action, a, s.Facing, s.IsSkill);          // 定点：每锚点各自一炸
        }
        finally { s.Source.NextAttackMultiplier = saved; }
    }

    /// <summary>射线路径末端格（视效画光束用）：Shape（省略=当前攻击范围）世界格子中离塔最远者。</summary>
    private static CellPos BeamEnd(Tower src, ActionSpec action)
    {
        var best = src.Cell;
        var bestD = -1.0;
        foreach (var c in (action.Target.Shape ?? src.EffectiveAttackRange).ToWorld(src.Cell, src.Facing))
        {
            var d = c.DistanceTo(src.Cell);
            if (d > bestD) { bestD = d; best = c; }
        }
        return best;
    }

    // ---------------------------------------------------------------- 击退（基础能力：与攻击力无关，距离为事先填入的格数）

    /// <summary>
    /// 击退：把敌人沿路径推回 N 格（PathDist 回退，下限出生点）。强网格语义下最干净的表达——
    /// 不产生离路径位移、无需重新寻路，被减速/束缚/眩晕的敌人同样可被推动。
    /// </summary>
    public void Knockback(Enemy e, double cells, Tower? source = null)
    {
        if (!e.Alive || cells <= 0)
            return;
        var from = e.PathDist;
        e.PathDist = Math.Max(0, e.PathDist - cells);
        if (source is not null)
            PushLog($"{e.Blueprint.Name} 被 {source.Blueprint.Name} 击退 {cells:0.#} 格（{from:0.#} → {e.PathDist:0.#}）");
    }

    // ---------------------------------------------------------------- 效果传导（衰减副本在目标间传递）

    /// <summary>
    /// 传导：主效果结算后，其衰减副本（Amount/Add 按 Falloff^i 缩放）从主目标出发，
    /// 在 Chain.Radius 范围内逐跳传递（阵营沿用该攻击的 TargetSide，策略取沿路径最前者），
    /// 共 Times 跳；AllowRepeat=false 时已命中目标不再接收。
    /// </summary>
    private void ChainEffect(Tower src, Entity origin, EffectSpec spec, ChainSpec chain, TargetSide side)
    {
        var scale = 1.0;
        var carry = origin;
        var hit = new HashSet<Entity> { origin };
        for (var i = 0; i < chain.Times; i++)
        {
            scale *= chain.Falloff;
            var cells = chain.Radius.ToWorldSet(CellOf(carry), src.Facing);
            Entity? next = side switch
            {
                TargetSide.Enemy => Pick(new TargetSpec(side, Pick: TargetPick.First),
                    Enemies.Where(e => e.Alive && cells.Contains(e.Cell) && (chain.AllowRepeat || !hit.Contains(e))),
                    pathPos: e => e.DistToExit, hp: e => e.Hp).FirstOrDefault(),
                _ => Pick(new TargetSpec(side, Pick: TargetPick.First),
                    Towers.Where(t => t.Alive && t != src && cells.Contains(t.Cell) && (chain.AllowRepeat || !hit.Contains(t))),
                    pathPos: t => t.Cell.DistanceTo(CellOf(carry)), hp: t => t.Hp).FirstOrDefault(),
            };
            if (next is null)
                break; // 范围内无可传递者——链条中断
            var weakened = spec with
            {
                Chain = null, // 副本不再传导（防无限递归）
                Amount = spec.Amount * scale,
                Add = spec.Add * scale,
                Mul = 1 + (spec.Mul - 1) * scale, // 乘区按比例缩放（1 为无变化基准）
            };
            EffectRegistry.Apply(this, src, next, weakened);
            hit.Add(next);
            carry = next;
            PushLog($"{src.Blueprint.Name} 的【{spec.Kind}】传导至 {NameOf(next)}（第 {i + 1} 跳，剩余 {scale:0%}）");
        }
    }

    // ---------------------------------------------------------------- 事件盒（感知事件的开盒触发器）

    private void UpdateTrapBoxes(double dt)
    {
        // 定时盒倒计时开盒；事件盒的存留上限归零则未触发消散
        foreach (var carrier in Enemies.Concat<Entity>(Towers))
            foreach (var box in carrier.TrapBoxes.ToList())
            {
                if (box.Remaining <= 0)
                    continue; // 事件盒不限存留
                box.Remaining -= dt;
                if (box.Remaining <= 0)
                {
                    carrier.TrapBoxes.Remove(box);
                    if (box.TriggerEvent is null)
                        OpenBox(box); // 定时盒：到点开盒
                    else
                        PushLog($"{box.Source.Blueprint.Name} 的 {NameOf(box.Carrier)} 的事件盒过期消散");
                }
            }
    }

    /// <summary>开盒：以携带者位置为锚，对装填的 Payload 做区域结算（效果可任意组合）。</summary>
    private void OpenBox(TrapBox box)
    {
        DetonateZone(box.Source, box.Payload, CellOf(box.Carrier), box.Source.Facing, isSkill: false);
        Publish(new SimEvent { Name = "box.open", Tower = box.Source, Cell = CellOf(box.Carrier) });
        PushLog($"{box.Source.Blueprint.Name} 的事件盒在 {NameOf(box.Carrier)} 身上开启");
    }

    /// <summary>事件依据检查：盒子只监听"携带者自己的事件"——只查事件主语实体身上的盒子（主语可能已死亡离场，盒子仍挂实体上）。</summary>
    private void CheckTrapBoxes(SimEvent e)
    {
        if (SubjectOf(e) is not { } subject)
            return;
        foreach (var box in subject.TrapBoxes.ToList())
            if (box.TriggerEvent == e.Name)
            {
                subject.TrapBoxes.Remove(box);
                OpenBox(box);
            }
    }

    /// <summary>事件主语实体（事件盒按主语过滤）。</summary>
    private static Entity? SubjectOf(SimEvent e) => (Entity?)e.Enemy ?? e.Tower;

    /// <summary>
    /// 【作用区间系统 · 公开可调用】以 anchor 格为锚、action.Delivery.Impact（null = 仅锚点格）为范围，
    /// 在区间内**当时在场**的实体中按 action.Target 的 Side/Pick 重新选取结算对象，然后结算 effects。
    /// 弹道命中/落地自动调用；持续地形效果、光环、mod 代码、事件被动都可以直接调用它实现"区域效果"。
    /// 无论区间内是否有目标都会发布 projectile.impact 事件（视效/被动扳机可见"炸了个空"）。
    /// </summary>
    public void DetonateZone(Tower source, ActionSpec action, CellPos anchor, Direction facing, bool isSkill = false)
        => DetonateCells(source, action,
            (action.Delivery?.Impact ?? RangeShape.Self).ToWorldSet(anchor, facing).ToList(), anchor, isSkill);

    /// <summary>
    /// 【作用区间系统 · 多格入口】以任意格子集合为作用区间，在区间内**当时在场**的实体中按 TargetSpec
    /// 重新选取结算对象并结算。DetonateZone（单锚点+形状）是它的封装；召唤·定区、持续地形、光环、
    /// mod 代码都可以直接调用本入口实现"有限制作用区域"的塔设计。整片区间一次结算/一次事件/一次日志。
    /// 无论区间内是否有目标都发布 projectile.impact 事件（视效/被动扳机可见"炸了个空"）。
    /// </summary>
    public void DetonateCells(Tower source, ActionSpec action, IReadOnlyList<CellPos> cells, CellPos anchor, bool isSkill = false)
    {
        var set = cells.ToHashSet();
        var targets = action.Target.Side switch
        {
            TargetSide.Self => new List<Entity>(), // 以区域锚点结算自身无意义
            TargetSide.Enemy => Pick(action.Target, Enemies.Where(e => e.Alive && set.Contains(e.Cell)),
                pathPos: e => e.DistToExit, hp: e => e.Hp),
            _ => Pick(action.Target, Towers.Where(t => t.Alive && t != source && set.Contains(t.Cell)),
                pathPos: t => t.Cell.DistanceTo(anchor), hp: t => t.Hp),
        };
        Publish(new SimEvent
        {
            Name = GameEvents.ProjectileImpact,
            Tower = source,
            Cell = anchor,
            Data = isSkill ? "skill" : "normal",
        });
        if (targets.Count == 0)
        {
            PushLog($"{source.Blueprint.Name} 的作用区间 [{anchor.X},{anchor.Y}] 内无目标——空爆");
            return;
        }
        PerformAction(source, action, targets, isSkill, viaProjectile: true);
    }

    /// <summary>按 TargetSpec 搜索目标。Side 决定找敌人还是找友方塔。</summary>
    public List<Entity> FindTargets(Tower src, TargetSpec spec)
    {
        if (spec.Side == TargetSide.Self)
            return [src];

        // Shape 为 null = 跟随该塔当前生效的攻击范围（含射程档位）
        var cells = (spec.Shape ?? src.EffectiveAttackRange).ToWorldSet(src.Cell, src.Facing);

        if (spec.Side == TargetSide.Enemy)
        {
            var cands = Enemies.Where(e => e.Alive && cells.Contains(e.Cell));
            return Pick(spec, cands, pathPos: e => e.DistToExit, hp: e => e.Hp);
        }

        // 友方：塔↔塔能力的 1 对 1 表达。
        // Designated = 程序/玩家显式指定作用对象（Tower.DesignatedTarget）：
        // 未指定、已摧毁或不在作用范围内时不出手 —— 不做范围内的自动索敌兜底。
        if (spec.Pick == TargetPick.Designated)
        {
            var d = src.DesignatedTarget;
            return d is { Alive: true } && d != src && cells.Contains(d.Cell) ? [d] : [];
        }

        var allies = Towers.Where(t => t.Alive && t != src && cells.Contains(t.Cell));
        return Pick(spec, allies, pathPos: t => t.Cell.DistanceTo(src.Cell), hp: t => t.Hp);
    }

    private static List<Entity> Pick<T>(TargetSpec spec, IEnumerable<T> cands, Func<T, double> pathPos, Func<T, double> hp)
        where T : Entity
    {
        var ordered = spec.Pick switch
        {
            // Designated 只对友方（Tower.DesignatedTarget）有意义，已在 FindTargets 提前处理；敌方按最近处理
            TargetPick.First or TargetPick.Designated => cands.OrderBy(pathPos),
            TargetPick.Strongest => cands.OrderByDescending(hp),
            TargetPick.Weakest => cands.OrderBy(hp),
            _ => cands.AsEnumerable(), // All
        };
        // MaxCount 对任意策略生效 = 单次攻击最多锁定的目标数（多目标索敌）。
        // 省略（∞ 哨兵）时：All = 全部；其余策略 = 1（单目标，向后兼容）。
        var count = spec.Pick switch
        {
            TargetPick.All => spec.MaxCount,
            TargetPick.Designated => 1, // 指定目标天然单对象
            _ => spec.MaxCount == int.MaxValue ? 1 : spec.MaxCount,
        };
        return ordered.Take(count).Cast<Entity>().ToList();
    }

    // ---------------------------------------------------------------- 数值结算（供效果处理器调用）

    /// <summary>
    /// 【伤害减免算法缺口】从"原始伤害"到"实际扣血"的整条管道，整体可装填替换——
    /// 减免算法不是本脚本的任务，脚本只承诺：调用时机（伤害发生时）与签名
    /// (目标敌人, 原始伤害, 类型, 来源塔) → 实际扣血量。
    /// 默认装填物（DefaultMitigate，仅保证演示可跑）：物理 = 扣 Defense（下限 5%）；
    /// 法术 = ×(1-SpellResist)；真实 = 无视；之后叠乘状态乘区（vulnerable/resist）。
    /// 目标游戏 / 测试性算法直接覆盖本属性即可（如换百分比护甲、护甲穿透、保底免伤等），
    /// Defense/SpellResist 只是数据面提供的原料，自定义算法可读可忽略。每局 NewGame 重置为默认。
    /// </summary>
    public Func<Enemy, double, DamageType, Tower?, double> Mitigate { get; set; } = DefaultMitigate;

    /// <summary>默认减免装填物：类型减免（物理扣 Defense 下限 5% / 法术乘 SpellResist / 真实无视）→ 叠乘状态乘区。</summary>
    public static double DefaultMitigate(Enemy e, double amount, DamageType type, Tower? source)
    {
        var mitigated = type switch
        {
            DamageType.Physical => Math.Max(amount - e.Blueprint.Defense, amount * 0.05),
            DamageType.Spell => amount * (1 - Math.Clamp(e.Blueprint.SpellResist, 0, 1)),
            DamageType.True => amount,
            _ => amount,
        };
        return mitigated * e.DamageTakenMultiplier;
    }

    public void DamageEnemy(Enemy e, double amount, Tower? source, DamageType type = DamageType.Physical)
    {
        if (!e.Alive || amount <= 0)
            return;
        var taken = Mitigate(e, amount, type, source); // 减免算法缺口（默认装填，可整体替换）
        e.Hp -= taken;
        Publish(new SimEvent { Name = GameEvents.EnemyHit, Enemy = e, Tower = source, Amount = taken, Cell = e.Cell });
        if (e.Hp <= 0)
        {
            e.Hp = 0;
            Enemies.Remove(e);
            Publish(new SimEvent { Name = GameEvents.EnemyDeath, Enemy = e, Tower = source, Amount = taken, Cell = e.Cell, Message = $"击杀 {e.Blueprint.Name}" });
            e.TrapBoxes.Clear(); // 死亡触发盒已在上面的事件分发中开启；未触发的随载体消散
        }
    }

    public void HealEnemy(Enemy e, double amount)
    {
        if (!e.Alive)
            return;
        e.Hp = Math.Min(e.Blueprint.MaxHp * EnemyHpScale, e.Hp + amount);
    }

    public void DamageTower(Tower t, double amount, Enemy? source = null)
    {
        if (!t.Alive || amount <= 0)
            return;
        t.Hp -= amount;
        Publish(new SimEvent { Name = GameEvents.TowerDamaged, Tower = t, Enemy = source, Amount = amount, Cell = t.Cell });
        if (t.Hp <= 0)
        {
            t.Hp = 0;
            Towers.Remove(t);
            foreach (var other in Towers)
                if (other.DesignatedTarget == t)
                    other.DesignatedTarget = null;
            Publish(new SimEvent { Name = GameEvents.TowerDestroyed, Tower = t, Cell = t.Cell, Message = $"{t.Name} 被摧毁" });
            t.TrapBoxes.Clear(); // 摧毁触发盒已在上面的事件分发中开启；未触发的随载体消散
            PushLog($"{t.Name} 被摧毁！");
        }
    }

    public void HealTower(Tower t, double amount)
    {
        if (!t.Alive)
            return;
        t.Hp = Math.Min(t.MaxHp, t.Hp + amount);
    }

    /// <summary>
    /// 把一条状态写到敌人身上（外部导入：效果处理器 / mod 代码调用）。
    /// 同状态重复施加 = 时长取更长者、强度取后一次的值；是否生效由 Enemy 的解释点决定。
    /// </summary>
    public void ApplyEnemyStatus(Enemy e, string stat, double duration, double mul = 1, double amount = 0)
    {
        if (!e.Alive)
            return;
        var wasStunned = e.IsStunned;
        var existing = e.Modifiers.FirstOrDefault(m => m.Stat == stat);
        if (existing is not null)
            e.Modifiers.Remove(existing);
        e.Modifiers.Add(new Buff
        {
            Stat = stat,
            Mul = mul,
            Amount = amount,
            Remaining = Math.Max(existing?.Remaining ?? 0, duration),
        });

        // 眩晕首次上身时发布事件（供视效/联动）
        if (stat == "stun" && !wasStunned)
            Publish(new SimEvent { Name = GameEvents.EnemyStunned, Enemy = e, Amount = duration, Cell = e.Cell });
    }

    // ---------------------------------------------------------------- 事件发布（含技力回复桥）

    /// <summary>
    /// 所有事件经由这里发布：①按蓝图的 SpOnEvent 给主语塔回复技力（mod 自定义事件同样生效）；
    /// ②分发事件触发型被动（主语塔监听该事件 → 按概率结算一次 Action，被动内不再嵌套触发被动）；
    /// ③派发给总线订阅者。
    /// </summary>
    public void Publish(SimEvent e)
    {
        // 事件桥：主语塔的每个技能按各自的 SpOnEvent 表独立回复技力（mod 自定义事件同样生效）
        if (e.Tower is { Alive: true } tower)
            foreach (var sk in tower.Skills)
                if (sk.Spec.SpOnEvent.TryGetValue(e.Name, out var gain))
                    sk.GainSp(gain);

        if (!_inPassive && e.Tower is { Alive: true } source && source.Blueprint.Passives.Count > 0)
        {
            foreach (var passive in source.Blueprint.Passives)
            {
                if (passive.EventName != e.Name || Rng.NextDouble() >= passive.Chance)
                    continue;
                if (!string.IsNullOrEmpty(passive.DisplayName))
                    PushLog($"{source.Name} 被动【{passive.DisplayName}】触发");
                _inPassive = true;
                try
                {
                    var targets = FindTargets(source, passive.Action.Target);
                    if (targets.Count > 0)
                        PerformAction(source, passive.Action, targets, isSkill: true);
                }
                finally
                {
                    _inPassive = false;
                }
            }
        }

        CheckTrapBoxes(e); // 事件盒：监听的事件发生且主语=携带者时开盒

        Events.Publish(e);
    }

    private bool _inPassive; // 防递归：被动引发的事件不再触发被动
}
