---
name: config-id-lookup
description: 将配置 ID 翻译为名称：门客、道具、红颜、商品、皮肤、系统、建筑、活动及任意 Luban 配置表。离线读取 Assets/GameRes/Config/*.bytes，活动名称结合运行时下发配置。用于排查数据时把 ID 列表转成可读名称。
---

# 配置 ID 查名称

```powershell
& C:/Users/YANQU/.codex/skills/config-id-lookup/scripts/cfg.ps1 <cmd> <类型或表名> [参数] [-Full]
```

- 用游戏实际使用的 `Assets/GameRes/Config/<表>Cfg.bytes` + `Library/ScriptAssemblies/GamePlay.dll` 中的生成类解析，与客户端运行时结果一致；不需要 Unity 运行（活动除外）
- 首次运行会用 Unity 自带编译器生成 `Library/ConfigLookup/ConfigLookup.exe`，之后秒级返回
- 返回的 `name` 是表内简体中文原文，未走多语言

## 类型

| 别名 | 表 | 主键 | 说明 |
|---|---|---|---|
| `partner` | Partner | 门客 ID | |
| `item` | Item | 道具 ID | |
| `beauty` | BeautyInfo | 红颜 ID | |
| `mall` / `goods` | Mall | 商品配置 ID | 商品名；`-Full` 看 `Reward`（实际道具）和 `Price` |
| `skin` | Appearance | 外观配置 ID | 协议里的 `xxxModelId` 是模型 ID，用 `where skin ModelId <id>` |
| `system` | GameSystem | 系统类型 | 系统开放、活动 Type 都对应这里 |
| `building` | BuildingInfo | 建筑 ID | |
| `word` | LanguageWord | 字符串 key | 取 `Name_zh_0` |
| — | 任意 `TbXxx` | — | 直接写 `Xxx`，`tables` 列出全部 |

## 命令

```powershell
cfg.ps1 get partner 840014,840010        # 多个 ID 用逗号或空格分隔
cfg.ps1 get mall 102410400 -Full         # 输出全部字段
cfg.ps1 find beauty 婉娘 [max]           # 任意字符串字段包含关键字（含描述），默认最多 50 条
cfg.ps1 where skin ModelId 56003         # 字段等于指定值
cfg.ps1 tables                           # 可用表与别名
cfg.ps1 activity [9010264,9010316]       # 活动，省略 ID 时列出当前全部
```

输出：`{"table":"TbXxx","items":[{"id":"...","found":true,"name":"..."}]}`；`found:false` 表示当前配置中不存在该 ID。

## 活动

活动配置由服务器下发，需要 Unity 处于 Play 模式（依赖 `runtime-inspect`）：

1. 从 `ActivityDataModel.ActMainConfigs` 取 `activityId` 对应的 `Type`、`ChildType` 与时间
2. `systemName` = GameSystem 表 `Type` 的名称；`activityName` = LanguageWord `Activity_Name-{Type}-{ChildType}`（部分活动无此 key）
3. `notDelivered` 列出服务器未下发的 ID（已结束、未开放或不在本服）

## 注意

- 名称以当前工作区的 `.bytes` 为准，切分支或更新配置后结果随之变化
- `GamePlay.dll` 过旧（Unity 未重新编译）而表结构已变更时，解析会报错或字段错位，先在 Unity 中完成编译
- 同一数字可能在多张表中都存在（如商品与道具），必须按来源字段选择类型
- 找不到 Unity 时设置环境变量 `UNITY_EDITOR_DIR` 指向 `Unity.exe` 所在目录


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
