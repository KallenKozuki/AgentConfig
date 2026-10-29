---
name: new-activity-scaffold
description: 新活动客户端接入骨架：协议生成、Model 分部类（DataModel、Event、RedDot、Helper、Helper.Config、Const）、活动红点、系统枚举、主界面入口、帮助说明、UI，并附带活动边界状态验收清单。用于新增活动、接入活动、新活动系统、活动入口、活动红点、活动参数配置解析，或排查活动跨天、切换账号、换期、配置缺失等问题。
---

# 新活动接入骨架

- 样板：全服拼团 `Model/GroupBuy/`、`UILogic/ActGift/GroupBuy/`；下西洋 `Model/WestOcean/`（红点和 `Helper.Config` 以下西洋为准）
- 文件模板见 [reference.md](reference.md)
- 动手前遵守 `AGENTS.md` 中的规则：UI Controller 结构规范、判空约定、服务端配置文案本地化、C# 注释规范；涉及组队时按 `common-team-architecture` skill 调用 `CommonTeamHelper`

## 接入清单

```
- [ ] 1. 协议：生成代码，并读 proto 注释
- [ ] 2. 系统枚举 GameSystemType
- [ ] 3. Model/Xxx/ 分部类
- [ ] 4. Entry.cs 注册 Model
- [ ] 5. 活动发现与数据请求入口
- [ ] 6. 活动红点
- [ ] 7. 主界面入口
- [ ] 8. 帮助说明
- [ ] 9. UI Ctrl / Model
- [ ] 10. 边界状态验收
```

### 1. 协议（工具完成）

按 `protocol-proto-lookup` 执行 `Tools/生成协议`。协议类、`EventArgs/Protocol/*EventArgs.cs`、`NetWorkManager.Ext.cs` 均为生成产物，不手写。写 Model 前先读 proto 注释，弄清每个字段和请求时机。

### 2. 系统枚举

`Model/SystemOpen/Enum.SystemOpen.cs` 的 `GameSystemType` 新增 `Xxx = 系统id, // 中文名`。多个活动共用一个入口时，在 `Const.ActivityAliasDict` 配置别名。

### 3. Model 分部类

目录 `Assets/Scripts/GamePlay/Model/Xxx/`：

| 文件 | 职责 |
|---|---|
| `Const.Xxx.cs` | `public static partial class Const`：本活动常量、本地存储 key 前缀 |
| `XxxDataModel.cs` | 继承 `ModelBase`：缓存字段、`ActivityId`、`Init` / `Clear`、换期清理、查询接口 |
| `XxxDataModel.Event.cs` | `AddEventListener`、`#region 请求`、`#region 回包`、派发数据变更事件 |
| `XxxDataModel.RedDot.cs` | 活动红点，见第 6 步 |
| `XxxHelper.cs` | `public static partial class XxxHelper`：活动 id、开放阶段判断、资源名计算等与 UI 无关的逻辑 |
| `XxxHelper.Config.cs` | **解析活动参数表的常量配置字段**，见下文 |
| `EventArgs/Xxx/XxxDataChangedEventArgs.cs` | 客户端自定义事件：Model 数据变化后通知 UI 刷新 |

DataModel 约定：

- 以 `ActivityId` 标识当前缓存属于哪一期；`PrepareActivity(activityId)` 发现 id 变化时先 `ClearData()`
- 回包先判 `Ret`，再用 `IsCurrentActivityResponse(activityId)` 丢弃非当前期的回包
- 回包写入数据后：刷新受影响的红点，再派发 `XxxDataChangedEventArgs`；UI 只监听该事件，不直接改 Model
- 缓存协议对象需要长期持有时克隆一份，避免回包对象回收后被复用
- Model 内的定时器 id 存在 Model 字段中，`ClearData` 时 `StopTimer` 并清零

#### XxxHelper.Config.cs：活动参数常量

活动参数表（`ActParamConfig`）里的常量字段统一在这里解析，**不要散落在 DataModel 或 Ctrl 里**。

- 每个 key 对应一个 `public static` 属性，属性名与 key 同名（例如 `XIYANG_OPEN_TIME`），私有缓存字段为 `_KEY`
- 统一使用 `ConfigHelper.ParseActParamConfig*`：`ParseActParamConfig`（int）、`String`、`IntArray`、`StringArray`、`Int2DArray`、`Int3DArray`；需要原始字符串时使用 `ActivityHelper.GetActParam`
- 配置缺失时，数组类返回 `null`，`ParseActParamConfig`（int）返回 0：**只缓存校验通过的结果**（非 `null` 且长度满足、int 非 0），否则下次重新解析，避免回包慢时空数据被缓存住
- 调用方必须判空、校验长度
- 样板：`WestOceanHelper.Config.cs` 的"活动通用配置"区
- 静态缓存不会随活动 id 变化自动清空；活动可能在同一次登录内换期且参数会变时，需在 DataModel 的换期处自行清理 Helper 缓存

### 4. 注册 Model

`Assets/Scripts/GamePlay/Entry.cs` 的 Model 初始化列表中加入 `ModelManager.Instance.GetModel<XxxDataModel>();`（放在 `MiscDataModel` 之前）。`Clear` 会在切换账号或重登时由 `ModelManager.ClearAllModel` 调用。

### 5. 活动发现与数据请求

二选一：

- **登录批量请求**：在 `ActivityHelper.DelayRequestAcitivity` 中按已有格式新增一段（先 `await UniTask.WaitForSeconds(0.1f)` 错开），对 `GetActiveActCfg(GameSystemType.Xxx)` 中已开放的活动请求配置和数据；参考"拼团活动"段
- **监听活动列表**：监听 `PlayerActRunningListEventArgs`，活动 id 变化时请求配置；系统未解锁时先记录待请求 id，`SystemOpenEventArgs` 命中后再补请求；参考 `WestOceanDataModel.OnPlayerRunningListEvent`

### 6. 活动红点

通用机制（树、样式、刷新时机、UI 挂载）见 `reddot-integration`，以下只列活动特有部分。样板：`WestOceanDataModel.RedDot.cs`、`GroupBuyDataModel.RedDot.cs`。

- 路径定义在 `RedDotName.cs` 的活动区：`"ActivityRoot/{0}/{1}/{2}/Xxx.../NormalRedDot"`，`{0}{1}{2}` 依次为别名、系统名、活动 id
- 路径一律通过 `ActivityHelper.GetRedDotName(activityId, [param1, [param2,]] format)` 生成；活动配置缺失时返回空，写入前判空
- 封装私有 `SetRedDot(string redDotName, bool value)`：活动 id 无效或路径为空时不写
- 首次查看红点：可用通用 `ActOpen`，或自定义 `XxxOpen` + `Const` 中的 key 前缀 + 活动 id，存 `UserDataModel`；提供 `MarkOpenViewed()`，在界面 `OnShow` 中调用
- 所有红点条件都要包含 `ActivityHelper.IsOpen(ActivityId)` 或阶段判断；活动结束或进入领奖期时写 0
- **阶段边界**：在下一个边界时间启动一次性定时器，到点后重新请求数据或清空玩法红点，然后调度下一次（`WestOceanDataModel.RefreshGameplayRedDotTimer`）
- **跨天**：按日数据记录所属日期 id，跨天后视为过期（`GroupBuyDataModel` 中的 `tierDataDayId` 及零点定时器）；也可监听 `ServerCrossDayEventArgs`
- 排行奖励红点：使用 `ActivityHelper.RecordMultiRankRewardRedDot`，并把系统加入 `ActivityDataModel.ShouldRecordMultiRankRewardRedDot`
- 活动商城免费礼包、任务、战令等通用模块已有 `ActShopFreeGift`、`ActTask`、`ActComBattlePass` 等节点，直接复用
- 背包相关红点：在 `BagItemUpdatedEventArgs` 中按道具 id 过滤后再刷新（`WestOceanDataModel.IsXxxUpdated` 系列）

### 7. 主界面入口

`UILogic/Main/Main/MainCtrl.Activity.cs`：

- 单系统单入口：`ActivityHelper.GetDynamicListDatas` 会自动处理；特殊入口参考 `AddGroupBuyActivityEntry`
- 入口按钮刷新：在系统类型 `switch` 中新增 case，把 `ActivityHelper.GetRedDotName(actId, RedDotName.ActIdRoot)` 挂到按钮上
- 点击跳转：`UIManager.Instance.ShowUI(UIType.XxxView, EUILayer.Middle, actMainConfig.ActivityId)`
- `UIType` 由 FGUI 导出生成（`FUIAutoGen/UIType.cs`），不要手改

### 8. 帮助说明

`Model/Rule/Const.Rule.cs` 新增 `XXX_RULE_ID = "规则id"; // 活动名`；界面中帮助按钮的回调调用 `ModelManager.Instance.GetModel<RuleDataModel>().TryShowRule(Model.Const.XXX_RULE_ID)`。

### 9. UI

`UILogic/<分类>/Xxx/XxxCtrl.cs` + `XxxModel.cs`，遵守 UI Controller 结构规范。`OnShow` 中依次：

1. 解析打开参数中的活动 id
2. 本地缺少活动基础配置时请求：`ActivityDataModel.ActBaseConfigDict` 中没有该 id 就调用 `RequestActConfig`，并监听 `PlayerActConfigEventArgs`，配置回来后重建界面（参考 `GroupBuyCtrl.RequestActivityConfigIfNeeded`）
3. 请求最新数据
4. 标记首次查看红点

数据刷新只监听 `XxxDataChangedEventArgs` 和本界面直接相关的协议事件。

## 边界状态验收

- [ ] **配置缺失或回包慢**：配置为空时界面不报错；`Helper.Config` 不缓存空结果，回包后能恢复
- [ ] **换期**：活动 id 变化时 DataModel 缓存已清空，红点按新 id 重建
- [ ] **跨天**：停留在界面内跨天，按日数据和红点会重置，必要时重新请求
- [ ] **阶段切换**：进入领奖期或休息期时入口状态、红点、按钮可用性正确；停留界面时收到错误码会关闭界面或返回主界面
- [ ] **切换账号**：`Clear` 清空数据、红点（含动态节点）和定时器，新账号无残留
- [ ] **系统未解锁**：不显示入口和红点，解锁后补请求
- [ ] **无资格玩家**：入口、战令、礼包等按资格隐藏
- [ ] **帮助说明、入口图标、规则文本**已接入
- [ ] **服务端文案**按本地化规则处理（`L10nText.GetTbWord` / `GetTbWordByZhName`）


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
