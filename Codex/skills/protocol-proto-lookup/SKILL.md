---
name: protocol-proto-lookup
description: 生成客户端协议代码，并到 proto 源文件中查阅协议、字段、命令号、错误码的注释。用于接入新协议、执行 Tools/生成协议、理解 e.Message 字段含义、查 CmdEnum 或 ErrorCodeEnum 含义，或在 Protocol 目录下的生成代码中找不到注释时。
---

# 协议生成与 proto 注释查阅

`Assets/Scripts/GamePlay/Protocol/*.cs` 由工具从 proto 生成，**不带任何业务注释**。字段语义、单位、取值范围、请求时机一律以 proto 源文件注释为准，不能只根据字段名推测。

## proto 源目录

- 仓库：项目同级目录 `../proto`（远端 `zhuixu_pro/proto.git`）
- 源文件：`../proto/protogen/proto/**/*.proto`
  - 活动协议在 `activity/` 子目录，例如 `activity/GroupBuy.proto`
  - 命令号：`Cmd.proto`；错误码：`ErrorCode.proto`；通用枚举：`CommonEnum.proto`；通用结构：`Common.proto`、`Common2.proto`
- 路径失效时按此链路重新解析：
  1. `Assets/Editor/ToolEditor/ToolsEditor.cs` 中的 `genProtocolBatName`（当前为 `gen_protocol2.bat`，位于项目根目录）
  2. 该 bat 先 `cd ../proto/protogen` 并调用 `protocol2.bat`
  3. `protocol2.bat` 中 `PROTO_ROOT=%SCRIPT_DIR%\proto` 即为源目录
- 查阅前若需要最新协议，先在 `../proto` 执行 `git pull`

## 生成协议（工具完成，禁止手写）

在 Unity 中执行菜单 `Tools/生成协议`，工具依次完成：

1. 编译 proto 到 `../proto/protogen/output/csharp`，再复制到 `Assets/Scripts/GamePlay/Protocol`（跳过 `Slg*`、`Dart.cs`）
2. 编译结束后自动执行 `Tools/生成协议后处理`：给协议类补 `IReference` 接口和 `Clear()`
3. 读取 `Cmd.cs` 中的 `CmdEnum`，对每个 `CS_Xxx`：
   - 存在 `XxxRsp` 时，按 `Assets/Editor/ToolEditor/ProtocolEventTemp.text` 生成 `EventArgs/Protocol/XxxEventArgs.cs`
   - 整体重写 `Network/NetWorkManager.Ext.cs`：命令号与 `XxxRsp`、`XxxEventArgs`、`XxxReq` 的映射和分发方法

约束：

- 以下文件全部为生成产物，**不要手改**：`Protocol/*.cs`、`EventArgs/Protocol/*EventArgs.cs`、`Network/NetWorkManager.Ext.cs`
- 协议类需要扩展时，使用 partial class 写在 `Protocol/Extensions/`
- 新协议没有生成事件参数时，先检查 proto 命名是否符合约定：命令为 `CS_Xxx`，回包 message 为 `XxxRsp`，请求为 `XxxReq`；服务端推送同样遵循 `CS_ServerPushXxx` 对应 `ServerPushXxxRsp`
- 菜单执行后若编译回调没有触发后处理，手动执行 `Tools/生成协议后处理`

## C# 与 proto 的名称对应

| C# 生成代码 | proto 源 |
|---|---|
| 文件头 `// Input: GroupBuy.proto` | 对应的 proto 文件名，可能位于 `activity/` 子目录 |
| 类 `PlayerGroupBuyDataRsp` | `message PlayerGroupBuyDataRsp` |
| 属性 `ClaimedNodeIds` | 字段 `claimed_node_ids`（snake_case 转 PascalCase） |
| `CmdEnum.CSPlayerGroupBuyConfig` | `Cmd.proto` 中 `CS_PlayerGroupBuyConfig = 23200; // 注释` |
| `PlayerGroupBuyConfigEventArgs` | 去掉 `CS_` 前缀的命令名 + `EventArgs`，`Message` 类型为对应的 `Rsp` |
| `ErrorCodeEnum.Xxx` | `ErrorCode.proto` 中的同名枚举项 |

## 查阅流程

1. 从 C# 类型找到 proto 文件：读生成文件头的 `// Input:`，或在 `../proto/protogen/proto` 中用 rg 搜 `message 类名`
2. 读三类注释：
   - message 上方的块注释：用途，通常带命令号，例如 `// 获取指定档位的当日进度数据 23201`
   - 字段行尾注释：语义、单位、格式（例如 `itemId=count|itemId=count`）
   - `Cmd.proto` 命令行尾注释：请求或推送的触发时机
3. 字段引用其他 message 或枚举时，继续追到定义处读注释
4. 注意 `optional` 字段：未下发时 getter 返回默认值；需要区分"未下发"和"值为 0"时使用生成的 `ShouldSerializeXxx()`
5. 回包统一先判断 `Message.Ret != (int)ErrorCodeEnum.ErrNone`；需要提示时使用 `UIHelper.ShowErrorTip(ret)`

## 输出要求

向用户解释协议或据此写代码时：

- 列出用到的字段、类型和 proto 注释原意
- proto 没有注释的字段标注为"proto 无注释，需与服务端确认"，不要自行补全语义
- 引用来源时给出 proto 文件相对路径和 message 名


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
