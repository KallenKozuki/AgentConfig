---
name: common-team-architecture
description: 通用组队模块规范。当用户提到组队、队伍、小队、队员、组队邀请、队伍管理、组队功能、Team、Squad 等关键词，或请求实现队伍创建、加入队伍、退出队伍、查询队伍列表、队伍详情、队伍成员管理、队伍权限、有队伍功能的活动获取排行数据等相关功能时，必须使用此 skill。
---

# 通用组队模块规范

- 优先复用 `CommonTeamHelper` 实现通用组队功能；不要为具体活动重复实现通用队伍缓存、请求或状态判断。
- 业务逻辑和 UI 只能调用 `CommonTeamHelper` 的公开接口，不得直接引用或访问 `CommonTeamDataModel`、`CommonTeamActivityData`、`CommonTeamListRequestState`。
- 查询数据、发送协议请求、清理缓存均通过 `CommonTeamHelper`。缺少所需能力时，先在 `CommonTeamHelper` 增加语义明确的公开接口；数据模型的直接访问仅限 Helper 内部。
- 通过 Helper 获取的大厅队伍列表、申请 ID 集合及队伍数据均视为只读缓存：不得增删集合或修改队伍对象字段。需要更新时发送对应请求，并由协议回包更新缓存。
- 关闭活动的队伍大厅界面时，必须调用 `CommonTeamHelper.ClearTeamHallList(activityId)` 清理大厅列表与分页状态。
- 退出或解散队伍后需要展示旧队伍信息时，使用 `CommonTeamHelper.TakeLastSelfTeam(activityId)`；该接口为一次性读取，不能缓存其返回值供后续操作使用。
- 新增的 Helper 接口应按职责归入：查询接口、缓存管理接口、队伍领域扩展或协议请求接口。

## 有队伍功能的活动排行

- 队伍和成员排行缓存必须归属 `CommonTeamActivityData`，业务逻辑和 UI 不得自行缓存排行结果。
- UI 只能调用 `CommonTeamHelper.GetPlayerRank`、`GetTeamRank`、`RequestTeamDetailRanks` 等公开接口；不得直接调用 `ActivityDataModel.RequestActRankData` 或 `CommonTeamDataModel.RequestRankData`。
- `RequestSelfTeamData(activityId)` 负责同步自己的个人排行（`ReqType=1, Type=1`）和自己队伍排行（`ReqType=1, Type=3`）；队伍详情通过 `RequestTeamDetailRanks` 获取指定队伍及成员排行。
- 仅 `ReqType=TargetRank(4)` 可以填写 `TargetId`：`Type=Single(1)` 填 `playerId`，`Type=Team(3)` 填 `teamId`；其他请求不得填写。
- `PlayerActRankDataRsp` 仅在 `CommonTeamDataModel` 已存在该活动缓存时写入队伍排行缓存；同活动的其他模块排行回包可复用，无需按请求来源过滤。
- 排名值 `0` 表示未上榜或尚未返回；UI 必须通过 Helper 查询并展示对应空态，不能将 `0` 直接作为排名显示。
- 清理活动队伍缓存时，必须同时清理个人与队伍排行缓存。


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
