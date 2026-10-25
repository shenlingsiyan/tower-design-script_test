---
name: tower-design-script
description: 用 TowerGrid Core 数据蓝图设计强网格塔防的防御塔。当需要创建、修改或审查防御塔定义（C# 蓝图或 JSON 模组）、组合攻击效果与投递方式、设计技力技能/被动/事件盒、或排查塔行为不符合预期时使用。
---

# TowerGrid Core 造塔技能

本库的造塔哲学：**塔 = 纯数据组合**。仿真内核（`src/`）不因内容而改动；做一座新塔只是填一份蓝图。任何"需要改内核才能实现的塔"，先检查是否已有对应模块组合。

## 造塔流程

1. **定基础面**：`maxHp / attackPower / attackInterval / blocks` + 攻击范围形状（`rect` 矩形含盲区、`line` 直线、`around` 周身；朝向四向旋转同一份数据）
2. **组普攻 ActionSpec**：
   - 打谁 → `TargetSpec`（side 敌/友/己 + pick 策略 + 可选 `maxCount` 锁多目标）
   - 打出什么 → `EffectSpec[]`（伤害/状态/DoT/buff/击退/事件盒…可任意叠加；`useSourceAttack: true` = 攻击力百分比转换，否则固定数值）
3. **选投递 DeliverySpec**：瞬发（省略）/ 弹幕（`homing` 死守、`homingRetarget` 择新、`directional` 目标性+落点作用区间）/ 射线 `beam` / 召唤 `summon`（`delay` + 定区 `area` 或定点锚定）
4. **加技能（可选）**：`skills[]` 各自独立技力条；来源 = 自然恢复 / `spOnEvent` 事件回复表 / `grant_sp` 效果；`trigger: auto|manual`；替换普攻模式 = 技能期普攻换成技能的 ActionSpec
5. **加被动（可选）**：`{ triggerEvent, chance, action }`——事件扳机触发的攻击
6. **JSON 化（推荐）**：内容一律写成 JSON 模组（参考 `examples/venom-tower.json`），`TowerModJson.Parse → Install` 装载；效果 kind / 条件 ID 拼错会在装载期报错，而不是运行期静默失效

## 组合速查

| 想表达的塔 | 组合 |
| --- | --- |
| 闪电链 | 任意伤害效果 + `chain: { radius, falloff, times }` |
| 死亡爆毒 | 普攻 + `trigger_box`（triggerEvent: enemy.death + payload 区域效果） |
| 定时炸弹 | `trigger_box` 无 triggerEvent（定时盒）+ payload |
| 击退炮 | 弹幕投递 + `knockback`（距离固定，与攻击力无关） |
| 散射导弹 | `maxCount: N`（N 目标各一发）+ `homingRetarget` |
| 齐射 | `multicast_next_attack`（同目标多发） |
| 减速光环/地形 | 召唤 `summon` 定区 + `slow`（作用区间系统 `DetonateZone/DetonateCells` 可直接复用） |
| 赋能/医疗塔（打友方） | `side: friendly` + `grant_sp` / `heal`（`useSourceAttack` 百分比转换） |
| 背水一战 | `buff` + `condition: hp_below:0.4` + `permanent: true` |
| 换射程档位 | `rangeTiers` + `set_range_tier` |

## 边界（刻意不做，属目标游戏侧）

- 减免算法：只提供 `Simulation.Mitigate` 装填缺口，不在脚本里写死公式
- 视觉/音效/UI：走事件总线 `Events` + `Log`，由宿主映射
- 经济、成长曲线、关卡、敌人侧 AI

## 验证方式

塔定义是纯数据，验证 = 挂到固定步长仿真跑：`NewGame → PlaceTower → Step(dt)`，用有种子 RNG 保证可复现；读 `Log` 与订阅事件断言行为（击退日志、传导跳数与衰减、事件盒开盒等均有日志输出）。
