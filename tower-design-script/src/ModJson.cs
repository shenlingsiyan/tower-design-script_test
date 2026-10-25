using System.Text.Json.Nodes;

namespace TowerGrid.Core;

/// <summary>JSON 模组解析错误（消息带字段路径，便于定位）。</summary>
public sealed class ModJsonException(string message) : Exception(message);

/// <summary>
/// JSON 数据模组加载器：不写 C# 也能定义新塔 / 新敌人。
/// 效果 Kind 必须已在 EffectRegistry 注册（内置 18 种 + 其他已装载模组注册的）。
/// 字段名 camelCase；target.range 可省略 = 沿用该塔的 attackRange（含档位切换）。
/// </summary>
public static class TowerModJson
{
    /// <summary>解析一段模组 JSON 文本。</summary>
    public static TowerMod Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            throw new ModJsonException($"JSON 语法错误：{ex.Message}");
        }
        if (root is null)
            throw new ModJsonException("顶层必须是对象 { ... }");

        var id = ReqString(root, "id", "模组");
        var towers = new List<TowerBlueprint>();
        if (root["towers"] is JsonArray towerArr)
        {
            for (var i = 0; i < towerArr.Count; i++)
                towers.Add(ParseTower(Obj(towerArr[i], $"towers[{i}]"), $"towers[{i}]"));
        }
        var enemies = new List<EnemyBlueprint>();
        if (root["enemies"] is JsonArray enemyArr)
        {
            for (var i = 0; i < enemyArr.Count; i++)
                enemies.Add(ParseEnemy(Obj(enemyArr[i], $"enemies[{i}]"), $"enemies[{i}]"));
        }
        return new TowerMod
        {
            Id = id,
            Name = OptString(root, "name") ?? id,
            Version = OptString(root, "version") ?? "1.0",
            Towers = towers,
            Enemies = enemies,
        };
    }

    // ---------------------------------------------------------------- 塔 / 敌人

    private static TowerBlueprint ParseTower(JsonObject o, string path)
    {
        var range = ParseRange(Obj(Req(o, "attackRange", path), path + ".attackRange"), path + ".attackRange");
        IReadOnlyList<RangeShape>? tiers = null;
        if (o["rangeTiers"] is JsonArray tierArr)
        {
            var list = new List<RangeShape>();
            for (var i = 0; i < tierArr.Count; i++)
                list.Add(ParseRange(Obj(tierArr[i], $"{path}.rangeTiers[{i}]"), $"{path}.rangeTiers[{i}]"));
            tiers = list;
        }
        IReadOnlyList<SkillSpec> skills = [];
        if (o["skills"] is JsonArray skillArr)
        {
            var list = new List<SkillSpec>();
            for (var i = 0; i < skillArr.Count; i++)
                list.Add(ParseSkill(Obj(skillArr[i], $"{path}.skills[{i}]"), $"{path}.skills[{i}]", range));
            skills = list;
        }
        else if (o["skill"] is { } single)
        {
            skills = [ParseSkill(Obj(single, path + ".skill"), path + ".skill", range)];
        }
        return new TowerBlueprint
        {
            Id = ReqString(o, "id", path),
            Name = ReqString(o, "name", path),
            Description = OptString(o, "description") ?? "",
            MaxHp = ReqNum(o, "maxHp", path),
            AttackPower = ReqNum(o, "attackPower", path),
            AttackInterval = ReqNum(o, "attackInterval", path),
            AttackRange = range,
            RangeTiers = tiers,
            NormalAttack = ParseAction(Obj(Req(o, "normalAttack", path), path + ".normalAttack"), path + ".normalAttack", range),
            Skills = skills,
            Passives = ParsePassives(o, path, range),
            BlocksPath = OptBool(o, "blocksPath") ?? true,
            Color = OptString(o, "color") ?? "#4fb8a8",
        };
    }

    private static EnemyBlueprint ParseEnemy(JsonObject o, string path) => new()
    {
        Id = ReqString(o, "id", path),
        Name = ReqString(o, "name", path),
        MaxHp = ReqNum(o, "maxHp", path),
        Speed = ReqNum(o, "speed", path),
        AttackPower = OptNum(o, "attackPower") ?? 0,
        AttackInterval = OptNum(o, "attackInterval") ?? 1,
        Defense = OptNum(o, "defense") ?? 0,           // 物理防御（flat 扣减）
        SpellResist = OptNum(o, "spellResist") ?? 0,   // 法术抗性（0~1 比例）
        Color = OptString(o, "color") ?? "#e05b4f",
    };

    // ---------------------------------------------------------------- 攻击 / 技能 / 被动

    private static ActionSpec ParseAction(JsonObject o, string path, RangeShape fallbackRange)
    {
        var target = o["target"] is { } t
            ? ParseTarget(Obj(t, path + ".target"), path + ".target", fallbackRange)
            : new TargetSpec(TargetSide.Enemy, fallbackRange);
        if (o["effects"] is not JsonArray effects || effects.Count == 0)
            throw new ModJsonException($"{path}.effects：至少需要一条效果");
        var list = new List<EffectSpec>();
        for (var i = 0; i < effects.Count; i++)
            list.Add(ParseEffect(Obj(effects[i], $"{path}.effects[{i}]"), $"{path}.effects[{i}]"));
        var delivery = o["delivery"] is { } d
            ? ParseDelivery(Obj(d, path + ".delivery"), path + ".delivery")
            : null; // 省略 = 瞬发（现行为）
        return new ActionSpec(target, list, delivery);
    }

    /// <summary>解析投递方式：kind（instant / homing / homingRetarget / directional / beam / summon）+ speed + impact + delay + area。</summary>
    private static DeliverySpec? ParseDelivery(JsonObject o, string path)
    {
        var kind = OptString(o, "kind")?.ToLowerInvariant() switch
        {
            null or "instant" => DeliveryKind.Instant,
            "homing" => DeliveryKind.Homing,                 // 弹幕型·指向性·死守：只认原定目标，目标死 → 弹道消失
            "homingretarget" => DeliveryKind.HomingRetarget, // 弹幕型·指向性·择新：目标死 → 按原索敌规则自动转追
            "directional" => DeliveryKind.Directional,       // 弹幕型·目标性：沿发射方向飞行、不跟随
            "beam" => DeliveryKind.Beam,                     // 射线型：发射即作用射线路径上所有指定类型单位
            "summon" => DeliveryKind.Summon,                 // 召唤型：发射时锁定落区，delay 秒后区域结算（无弹道无追踪）
            var s => throw new ModJsonException($"{path}.kind：“{s}”无法识别（可用 instant / homing / homingRetarget / directional / beam / summon）"),
        };
        var impact = o["impact"] is { } imp
            ? ParseRange(Obj(imp, path + ".impact"), path + ".impact")
            : null; // 省略 = 指向性作用于目标本身 / 目标性落点单格 / 召唤·定点每锚点单爆
        var area = o["area"] is { } ar
            ? ParseRange(Obj(ar, path + ".area"), path + ".area")
            : null; // 召唤型·定区落区（以塔为基准随朝向旋转）；省略 = 定点模式（锚定锁定目标的发射时位置）
        return new DeliverySpec
        {
            Kind = kind, Speed = OptNum(o, "speed") ?? 8, Impact = impact,
            Delay = OptNum(o, "delay") ?? 1, Area = area,
        };
    }

    private static TargetSpec ParseTarget(JsonObject o, string path, RangeShape fallbackRange)
    {
        var side = OptString(o, "side")?.ToLowerInvariant() switch
        {
            null or "enemy" => TargetSide.Enemy,
            "friendly" => TargetSide.Friendly,
            "self" => TargetSide.Self,
            var s => throw new ModJsonException($"{path}.side：“{s}”无法识别（可用 enemy / friendly / self）"),
        };
        var shape = o["range"] is { } r
            ? ParseRange(Obj(r, path + ".range"), path + ".range")
            : fallbackRange; // 省略 = 沿用该塔的 attackRange
        var pick = OptString(o, "pick")?.ToLowerInvariant() switch
        {
            null or "first" => TargetPick.First,
            "strongest" => TargetPick.Strongest,
            "weakest" => TargetPick.Weakest,
            "all" => TargetPick.All,
            "designated" => TargetPick.Designated,
            var s => throw new ModJsonException($"{path}.pick：“{s}”无法识别（可用 first / strongest / weakest / all / designated）"),
        };
        var maxCount = (int?)OptNum(o, "maxCount") ?? (pick == TargetPick.All ? int.MaxValue : 1);
        return new TargetSpec(side, shape, pick, maxCount);
    }

    private static EffectSpec ParseEffect(JsonObject o, string path)
    {
        var extra = new Dictionary<string, double>();
        if (o["extra"] is JsonObject extraObj)
            foreach (var (k, v) in extraObj)
                extra[k] = (double?)(v as JsonValue) ?? throw new ModJsonException($"{path}.extra.{k}：必须是数字");
        var condition = OptString(o, "condition");
        if (condition is not null && !ConditionRegistry.Known(condition))
            throw new ModJsonException($"{path}.condition：“{condition}”未注册（可用 ConditionRegistry.Register 或内置 hp_below:P / hp_above:P）");
        return new EffectSpec
        {
            Kind = ReqString(o, "kind", path),
            Amount = OptNum(o, "amount") ?? 1,
            Duration = OptNum(o, "duration") ?? 0,
            UseSourceAttack = OptBool(o, "useSourceAttack") ?? false,
            SpScaleByAttack = OptBool(o, "spScaleByAttack") ?? false,
            Stat = OptString(o, "stat"),
            Add = OptNum(o, "add") ?? 0,
            Mul = OptNum(o, "mul") ?? 1,
            DamageType = OptString(o, "damageType")?.ToLowerInvariant() switch
            {
                null or "physical" => DamageType.Physical,
                "spell" => DamageType.Spell,
                "true" => DamageType.True,
                var s => throw new ModJsonException($"{path}.damageType：“{s}”无法识别（可用 physical / spell / true）"),
            },
            Condition = condition,
            Permanent = OptBool(o, "permanent") ?? false,
            Chain = o["chain"] is { } ch ? ParseChain(Obj(ch, path + ".chain"), path + ".chain") : null,
            TriggerEvent = OptString(o, "triggerEvent"),
            Payload = o["payload"] is { } pl ? ParseAction(Obj(pl, path + ".payload"), path + ".payload", RangeShape.Self) : null,
            Extra = extra.Count > 0 ? extra : null,
        };
    }

    /// <summary>解析效果传导：radius（RangeShape 语法）+ falloff（每跳衰减乘数）+ times（传递次数）+ allowRepeat（可否重复命中）。</summary>
    private static ChainSpec ParseChain(JsonObject o, string path) => new()
    {
        Radius = ParseRange(Obj(o["radius"] ?? throw new ModJsonException($"{path}.radius：缺少必填字段"), path + ".radius"), path + ".radius"),
        Falloff = OptNum(o, "falloff") ?? 1,
        Times = (int)(OptNum(o, "times") ?? 1),
        AllowRepeat = OptBool(o, "allowRepeat") ?? false,
    };

    private static SkillSpec ParseSkill(JsonObject o, string path, RangeShape fallbackRange)
    {
        var spOnEvent = new Dictionary<string, double>();
        if (o["spOnEvent"] is JsonObject sp)
            foreach (var (k, v) in sp)
                spOnEvent[k] = (double?)(v as JsonValue) ?? throw new ModJsonException($"{path}.spOnEvent.{k}：必须是数字");
        return new SkillSpec
        {
            DisplayName = ReqString(o, "displayName", path),
            SpMax = ReqNum(o, "spMax", path),
            InitialSp = OptNum(o, "initialSp") ?? 0,
            SpRegenPerSecond = OptNum(o, "spRegenPerSecond") ?? 0,
            SpOnEvent = spOnEvent,
            Trigger = OptString(o, "trigger")?.ToLowerInvariant() switch
            {
                null or "auto" => SkillTriggerMode.Auto,
                "manual" => SkillTriggerMode.Manual,
                var s => throw new ModJsonException($"{path}.trigger：“{s}”无法识别（可用 auto / manual）"),
            },
            Duration = OptNum(o, "duration") ?? 0,
            ReplaceNormalAttack = OptBool(o, "replaceNormalAttack") ?? false,
            Action = ParseAction(Obj(Req(o, "action", path), path + ".action"), path + ".action", fallbackRange),
        };
    }

    private static IReadOnlyList<PassiveSpec> ParsePassives(JsonObject o, string path, RangeShape fallbackRange)
    {
        if (o["passives"] is not JsonArray arr)
            return [];
        var list = new List<PassiveSpec>();
        for (var i = 0; i < arr.Count; i++)
        {
            var p = Obj(arr[i], $"{path}.passives[{i}]");
            list.Add(new PassiveSpec
            {
                EventName = OptString(p, "event") ?? OptString(p, "eventName")
                    ?? throw new ModJsonException($"{path}.passives[{i}].event：被动必须指定监听事件名"),
                DisplayName = OptString(p, "displayName") ?? "",
                Chance = OptNum(p, "chance") ?? 1,
                Action = ParseAction(Obj(Req(p, "action", $"{path}.passives[{i}]"), $"{path}.passives[{i}].action"),
                    $"{path}.passives[{i}].action", fallbackRange),
            });
        }
        return list;
    }

    // ---------------------------------------------------------------- 攻击范围

    private static RangeShape ParseRange(JsonObject o, string path)
    {
        var kind = OptString(o, "kind")?.ToLowerInvariant() ?? "rect";
        switch (kind)
        {
            case "line":
                return RangeShape.Line((int)ReqNum(o, "forward", path));
            case "rect":
            {
                var min = (int)(OptNum(o, "min") ?? 1);
                var max = (int)ReqNum(o, "max", path);
                var half = (int)(OptNum(o, "halfWidth") ?? 0);
                return RangeShape.Rect(min, max, half);
            }
            case "around":
                return RangeShape.Around((int)ReqNum(o, "radius", path));
            case "self":
                return RangeShape.Self;
            case "of":
            {
                if (o["cells"] is not JsonArray cells || cells.Count == 0)
                    throw new ModJsonException($"{path}.cells：kind=of 需要非空的 [[F,S],...] 格子列表");
                var list = new List<(int F, int S)>();
                foreach (var c in cells)
                {
                    if (c is not JsonArray pair || pair.Count != 2)
                        throw new ModJsonException($"{path}.cells：每项必须是 [F,S] 两个数字");
                    list.Add(((int)(double)pair[0]!, (int)(double)pair[1]!));
                }
                return new RangeShape(list);
            }
            default:
                throw new ModJsonException($"{path}.kind：“{kind}”无法识别（可用 line / rect / around / self / of）");
        }
    }

    // ---------------------------------------------------------------- 基础取值

    private static JsonObject Obj(JsonNode? node, string path) =>
        node as JsonObject ?? throw new ModJsonException($"{path}：必须是对象 {{ ... }}");

    private static JsonNode Req(JsonObject o, string key, string path) =>
        o[key] ?? throw new ModJsonException($"{path}.{key}：缺少必填字段");

    private static string ReqString(JsonObject o, string key, string path)
    {
        var v = Req(o, key, path) as JsonValue;
        return (v is not null && v.TryGetValue<string>(out var s)) ? s
            : throw new ModJsonException($"{path}.{key}：必须是字符串");
    }

    private static double ReqNum(JsonObject o, string key, string path)
    {
        var v = (double?)Opt(o, key);
        return v ?? throw new ModJsonException($"{path}.{key}：缺少必填数字字段");
    }

    private static JsonNode? Opt(JsonObject o, string key) => o.ContainsKey(key) ? o[key] : null;

    private static string? OptString(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v || !v.TryGetValue<string>(out var s))
            return null;
        return s;
    }

    private static double? OptNum(JsonObject o, string key) => (double?)(o[key] as JsonValue);

    private static bool? OptBool(JsonObject o, string key) => (bool?)(o[key] as JsonValue);
}
