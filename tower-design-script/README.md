# TowerGrid Core · 防御塔设计脚本

为**强网格塔防**（正方形格子地图）设计的防御塔设计核心库。塔被定义为**纯数据蓝图**——程序角度制作一座防御塔 = 填数据、组合模块，不写仿真代码。固定步长仿真，无引擎依赖，可直接搬进 Unity / Godot / 服务器 / Blazor（只调 `Simulation.Step(dt)`）。

**它是**：防御塔设计脚本（蓝图数据、攻击效果、投递方式、技力条、被动与事件、JSON 模组、可装填的数值缺口）。
**它不是**：游戏、渲染库、经济/关卡/成长系统——这些属于目标游戏侧。

```
src/        核心脚本（8 个 .cs + csproj，net10.0，零第三方依赖）
examples/   JSON 模组示例（零代码造塔）
```

## 快速开始

```csharp
using TowerGrid.Core;

// 1) 定义一座塔（C# 蓝图；也可用 JSON，见下文）
var sniper = new TowerBlueprint
{
    Id = "sniper", Name = "狙击塔",
    MaxHp = 80, AttackPower = 14, AttackInterval = 2.0, Blocks = false,
    AttackRange = RangeShape.Rect(min: 3, max: 7, halfWidth: 0), // 3~7 格直线，近处盲区
    NormalAttack = new ActionSpec(
        new TargetSpec(TargetSide.Enemy, Pick: TargetPick.First),   // 打谁：最前沿敌人
        [new EffectSpec { Kind = "damage", UseSourceAttack = true }]), // 打出什么：攻击力伤害
        // Delivery 省略 = Instant 瞬发；也可配弹幕/射线/召唤，见下文
};

// 2) 注册并开局
TowerLibrary.Register(sniper);
var sim = new Simulation(GridMap.FromWaypoints([(0, 4), (10, 4)], 14, 9), seed: 1);
sim.NewGame();

// 3) 部署 + 固定步长驱动（游戏主循环里每帧调用）
sim.PlaceTower(TowerLibrary.Get("sniper"), new CellPos(4, 3), Direction.Right);
sim.Step(dt);

// 4) 读取日志/事件做表现层
foreach (var line in sim.Log) Console.WriteLine(line);
sim.Events.Subscribe(e => { /* 命中、击杀、技能、弹道等表现 */ });
```

## 核心概念

### 1. 塔 = 数据蓝图
`TowerBlueprint` 只有数值基础面（生命/攻击力/攻击间隔/是否阻挡）+ 攻击范围形状 + 普攻/技能/被动列表。**塔的个性不靠字段堆砌，靠下方各面的组合表达**——加新塔不改仿真代码。静态蓝图（注册表）与运行时实例（`Tower`）分离，支持热装卸。

### 2. 攻击 = ActionSpec
一次攻击拆成两个纯数据问题：
- **TargetSpec（打谁）**：阵营（敌/友塔/自身）+ 范围形状（省略=攻击范围）+ 挑选策略（`First/Strongest/Weakest/All`）+ `maxCount`（单次锁定 N 个目标，各发一发）
- **EffectSpec[]（打出什么）**：效果列表，见效果注册表

普攻、技能、被动**三者同构**，共用同一套解析与结算。

### 3. 投递 = DeliverySpec（六型）

| 型 | Kind | 行为 | 生效时刻 |
| --- | --- | --- | --- |
| 瞬发 | 省略 / `instant` | 无弹道 | 出手瞬间 |
| 弹幕·指向·死守 | `homing` | 追踪锁定目标；**目标死亡则弹道消失** | 命中实体 |
| 弹幕·指向·择新 | `homingRetarget` | 追踪；**目标死亡自动按原索敌规则转追** | 命中实体 |
| 弹幕·目标性 | `directional` | 沿发射向量直飞**不跟随**；目标可能跑出落点 | 落点作用区间 |
| 射线 | `beam` | 发射即作用射线路径上所有指定类型单位，无时限 | 出手瞬间 |
| 召唤 | `summon` | 不从塔位发射；`delay` 秒后在区域/目标位置显现 | 延迟到期 |

弹幕型可配 `speed` 与 `impact`（作用区间形状）；召唤型 `area` 省略=锚定锁定目标位置（定点），填写=以塔为基准的固定区域（定区）。

### 4. 作用区间（可被调用的系统）
`Simulation.DetonateZone(source, action, anchorCell, facing)` / `DetonateCells(...)`：给定锚点格（或任意格集合）与任意 `ActionSpec`，对**当时在场**的实体按 Side/Pick 重新选取并结算。弹道落地自动调用它；持续地形、区域光环、自定义落点规则直接复用。落点无目标时"空爆"并发布 `projectile.impact` 事件。

### 5. 技力条（特殊技能的资源面）
- 三种来源：自然恢复（`spRegenPerSecond`）、事件回复表（`spOnEvent`：任意事件名→回复量）、效果 `grant_sp`
- 触发模式：`Auto`（满自动释放）/ `Manual`（面板/按键触发）；可运行时覆盖
- **替换普攻模式**：技能期（`duration` 秒）内普攻被替换为技能的 ActionSpec，节拍沿用攻击间隔
- **一塔多技能**：`skills[]` 列表，各自独立技力条与触发模式

### 6. 被动与事件总线
`PassiveSpec = { TriggerEvent, Chance, Action }`——"以事件为扳机的攻击"（主语塔约定，防递归）。事件名全部开放字符串，mod 可自定义事件。常用：`tick`、`enemy.spawn`、`enemy.death`、`tower.attack`、`tower.skill`、`tower.placed`、`tower.destroyed`、`projectile.impact`。涌现联动示例：反击被动走 `tower.attack` 事件 → 事件回复表又给自身充技力。

### 7. 效果注册表（EffectRegistry）
状态集合对 mod 开放——可发明新状态而不碰敌人代码。内置效果按类：

| 类 | kind | 关键字段 |
| --- | --- | --- |
| 输出量 | `damage` `heal` `grant_sp` | `amount`、`useSourceAttack`（百分比转换：结算值 = 攻击力 × amount）、`damageType`（physical/spell/true） |
| 持续伤害 | `poison` `burn` | `amount`（每秒）、`duration` |
| 敌人状态 | `stun` `bind` `silence` `slow` `weaken` `vulnerable` `resist` `blind` `mark` | `mul`、`duration` |
| 塔服务 | `buff` `set_range_tier` | buff：`stat`（attack/attack_speed）、`add`、`mul`、`duration`、`condition`、`permanent` |
| 普攻修饰 | `empower_next_attack` `multicast_next_attack` | `mult` / 次数（下次普攻结算后清零） |
| 位移 | `knockback` | `amount`（推回格数，与攻击力无关） |
| 事件盒 | `trigger_box` | `triggerEvent`（null=定时盒）、`duration`、`payload`（完整 ActionSpec） |

任意效果可挂 `chain`（效果传导）：`{ radius, falloff, times, allowRepeat }`——主效果的衰减副本（每跳 ×falloff）在目标周围 `radius` 内逐跳传递 `times` 次。

### 事件盒（trigger_box）
防御塔给目标"发射一个感知事件的盒子"：盒子挂到目标身上，监听 `triggerEvent`（主语=携带者，如 `enemy.death`；留空 = 定时盒，`duration` 秒后开盒）。依据成立时开盒，以携带者为锚对 `payload`（任意效果组合）做区域结算；未触发的盒子随载体死亡消散。

## 数值语义

- **基础属性是唯一数值真理源**：攻击力/攻击间隔被 buff 调制后形成基础量（`EffectiveAttack/EffectiveInterval`），普攻、技能、充能、治疗按 `useSourceAttack` 百分比转换引用它——技能不引入第二套数值系统，只做转换
- **塔侧 buff 双旋钮**：`attack`（强度）/`attack_speed`（频率）；内容自由度（打谁+打出什么）归 ActionSpec 组合管，不归 buff 管
- **伤害类型三分 + 可装填缺口**：`DamageType` 只是随伤害流动的标签；**减免算法不是本脚本的任务**——`Simulation.Mitigate` 缺口整体可装填替换（签名：目标敌人, 原始伤害, 类型, 来源塔 → 实际扣血量）。默认装填物仅为演示可跑：物理=扣 Defense（下限 5%）、法术=×(1−SpellResist)、真实=无视，之后叠乘状态乘区（vulnerable/resist）。`Defense/SpellResist` 是数据面原料，自定义算法可读可忽略
- **条件型属性**：`buff` + `"condition": "hp_below:0.4"` + `"permanent": true` = 永不过期、条件满足期间生效的属性规则。条件 ID 走 `ConditionRegistry` 开放注册（内置 `hp_below:P` / `hp_above:P`），未注册 ID 在装载期报错

## JSON 模组（零代码造塔）

`TowerModJson.Parse(json)` → `TowerMod.Install()` 即装载，塔进入注册表。完整示例见 `examples/venom-tower.json`（含弹幕/召唤/事件盒/事件回复/射程档位等用法）。

```jsonc
{ "towers": [{
  "id": "venom", "name": "毒液塔",
  "maxHp": 100, "attackPower": 6, "attackInterval": 1.3,
  "color": "#6dd17c",
  "attackRange": { "kind": "rect", "min": 1, "max": 2, "halfWidth": 1 },
  "normalAttack": {
    "target": { "side": "enemy", "pick": "first" },
    "effects": [
      { "kind": "damage", "useSourceAttack": true },
      { "kind": "trigger_box", "triggerEvent": "enemy.death", "duration": 0,
        "payload": { "target": { "side": "enemy", "pick": "all",
                                 "shape": { "kind": "around", "radius": 1 } },
                     "effects": [ { "kind": "poison", "amount": 2, "duration": 3 } ] } }
    ],
    "delivery": { "kind": "homing", "speed": 6 }
  },
  "skills": [{ "name": "腐蚀之云", "spMax": 8, "spRegenPerSecond": 0.8,
               "trigger": "auto",
               "action": { "effects": [ { "kind": "vulnerable", "mul": 1.5, "duration": 4 } ],
                            "delivery": { "kind": "summon", "delay": 0.6,
                                           "area": { "kind": "around", "radius": 1 } } } }]
}] }
```

范围形状语法：`rect(min,max,halfWidth)` / `line(len)` / `around(radius)` / 手工格组。`rangeTiers` 支持离散射程档位（`set_range_tier` 切换）。

## 嵌入目标游戏（解释点清单）

脚本刻意把"目标游戏才能决定的事"留成缺口/注册表，接入时只改解释点、不动塔内容：

| 缺口/注册表 | 用途 |
| --- | --- |
| `Simulation.Mitigate` | 伤害减免算法（整体装填替换） |
| `Simulation.SeededRandom` | 有种子的确定性 RNG（可复现、可校验） |
| `EffectRegistry` | 新效果 kind（`Register("my_effect", handler)`） |
| `ConditionRegistry` | 新属性条件 ID |
| `TowerLibrary` / `TowerMod` | 塔蓝图注册与整体装卸 |
| 事件总线 `Events` | 订阅任意事件做表现层（弹道/特效/UI） |
| 敌人状态集合 | 开放字符串，含义由解释点定义 |

仿真不渲染、不读输入、不知道 UI——表现层全部由宿主通过事件总线与 `Log` 自行映射。

## License

以你选择的许可证为准（发布前补充 LICENSE 文件）。
