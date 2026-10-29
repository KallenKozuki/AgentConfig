---
name: runtime-inspect
description: 在 Unity Editor Play 模式下实时读取、搜索、修改 ModelManager 中各数据模块（Assets/Scripts/GamePlay/Model 下的 XxxDataModel）及已打开界面的 UIModel 字段值；监听字段变化历史；按 JSON 构造并派发客户端或服务器事件，返回各模块的响应报告。用于运行时排查 bug、核对协议下发数据、确认 Model 状态、验证 UI 显示与数据是否一致、临时改数据或模拟协议复现问题等场景；关键词：运行时数据、字段值、数据不对、Model 状态、UIModel、抓数据、改数据、监听变化、模拟协议、派发事件。
---

# 运行时检查

服务端位于 `Assets/Editor/RuntimeInspector/`（仅 Editor，随 Unity 启动自动监听 `127.0.0.1`）。通过客户端脚本访问，不要手写 HTTP 请求：

```powershell
& C:/Users/YANQU/.codex/skills/runtime-inspect/scripts/ri.ps1 <cmd> [参数]
```

脚本自动从 `Library/RuntimeInspector/port.txt` 读取端口（默认 17890，占用时顺延到 17899）。返回统一为 `{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`。

## 命令

| 命令 | 用途 | 常用参数 |
|---|---|---|
| `ping` | 服务状态、是否 Play、已创建模块数 | — |
| `models` | 列出已创建的模块 | `-Filter` 名称关键字 |
| `ui` | 列出已创建的界面（可见状态、Ctrl、UIModel 类型） | `-Filter` 名称关键字 |
| `get` | 读取模块或某个路径的值 | `-Name` 或 `-UI` `-Path` `-Depth` `-Limit` `-Props` |
| `search` | 按成员名 / 值关键字搜索路径 | `-Name` 或 `-UI`（都省略则搜全部数据模块）`-Path` `-Key` `-Value` `-Depth` `-Max` |
| `set` | 给路径赋值 | `-Name` 或 `-UI` `-Path` `-Value` / `-ValueFile` `-Merge` `-Force` `-Setter` |
| `col` | 集合增删清 | `-Name` 或 `-UI` `-Path` `-Op add/remove/clear` `-Key` `-Index` `-Value` |
| `watch add/list/history/remove/clear` | 字段变化监听，见下文 | `-Name` 或 `-UI` `-Path` `-Depth` `-Interval` `-Id` `-Full` |
| `event list/schema/dispatch` | 事件查询与派发，见下文 | `-Event` `-Filter` `-Value` / `-ValueFile` `-Wait` `-Watch` `-Depth` |

通用：`-Pretty` 缩进输出（人工阅读时用），`-Out file.json` 结果写文件（大数据时用），`-ValueFile` 从 UTF-8 文件读取 JSON（大段 JSON 时用，避免命令行转义）。

## 路径与名称

- `-Name`：模块类型名，忽略大小写；唯一包含匹配也可（`WestOcean` → `WestOceanDataModel`）
- `-UI`：界面名，读写该界面的 UIModel；可写 UIType 枚举名或界面组件类名（如 `MainView`），忽略大小写，唯一包含匹配也可
  - 只能访问当前已创建的界面（`ui` 命令列出的），未打开或已销毁时返回错误并列出已创建界面
  - 与 `-Name` 同时给出时以 `-UI` 为准
- `-Path`：`UserData.Event`、`itemDic[1001].Num`、`list[0]`、`dic["key with.dot"]`
  - 成员名用字段名或属性名均可（自动属性按属性名访问）；私有字段可直接访问
  - `[n]` 对 List / 数组是下标，对 Dictionary 是 key（按 key 类型自动转换，enum 可写名字）
- `search` 返回的 `path` 可直接用作 `get` / `set` 的 `-Path`；全模块搜索时形如 `XxxDataModel:path`，冒号前为 `-Name`

## 输出格式

- 普通对象：`{"$type":"类型名", 字段...}`；List → 数组；Dictionary → 对象
- 集合超过 `-Limit`（默认 50）：`{"$count":N,"$truncated":true,"$items":...}`
- 超出 `-Depth` 的节点为摘要字符串，如 `"<List<Item> count=12>"`、`"<Item>"`，需要时缩小 `-Path` 再读
- 其他标记：`"<cycle T>"` 循环引用、`"<delegate T xN>"` 委托订阅数、`"<error ...>"` 读取异常、`"<destroyed>"` 已销毁的 Unity 对象
- protobuf 消息只展示 `ProtoMember` 属性和扩展字段，不展示 `__pbn__` 内部字段
- 默认 depth：读整个模块为 2，指定 `-Path` 时为 3

## 排查流程

1. `ping`：确认 `playing=true`、`modelCount>0`。不在 Play 或未登录时先让用户进入游戏
2. `models -Filter 关键字` 确认目标模块已创建。模块只在首次 `GetModel` 后创建，不存在说明对应系统还未被访问过，本工具不会主动创建
3. 不确定字段名时先 `search -Name X -Key 关键字`；只知道某个 ID 时用 `search -Value 1001`
4. `get -Path` 精确读取，逐层下钻，避免对大模块直接加大 `-Depth`
5. 对照代码判断问题层级：协议回调写入是否正确 → Model 是否被意外清理 / 覆盖 → UI 读取或刷新逻辑
6. 需要对比前后变化时：操作前后各 `get` 一次（可 `-Out` 存文件再比较）

## 写入

```powershell
ri.ps1 set -Name WestOceanDataModel -Path 'UserData.Energy' -Value 100
ri.ps1 set -Name XxxDataModel -Path 'curState' -Value Finished          # enum 名字
ri.ps1 set -Name XxxDataModel -Path 'info' -Value '{"Num":5}' -Merge    # 只改部分字段
ri.ps1 col -Name XxxDataModel -Path 'itemDic' -Op add -Key 1001 -Value '{"Id":1001,"Num":5}'
ri.ps1 col -Name XxxDataModel -Path 'list' -Op remove -Index 0
```

- `-Value` 为 JSON 文本；非 JSON 文本按字符串处理
- 对象赋值默认整体替换（新实例，未给出的字段为默认值）；`-Merge` 在现有实例上只改给出的字段
- `readonly` 字段默认拒绝，`-Force` 强制写入；`-Setter` 改为调用属性 setter（会触发属性变更通知等 setter 内逻辑）
- 路径经过 struct 时会自动逐级写回
- 每次写入在 Unity Console 输出 `[RuntimeInspector]` 日志（含旧值、新值）

**写入限制**（必须告知用户）：
- 只改客户端内存，不通知服务器，不派发 EventManager 事件，UI 不会自动刷新，需重新打开界面或触发刷新
- 下次协议同步会覆盖修改；仅用于复现和验证，不能作为修复手段
- 写入前先 `get` 记录原值，验证结束后恢复或提示用户重启 Play

## 变化监听

```powershell
ri.ps1 watch add -Name AssistantDataModel -Path 'ServerSettings' -Depth 3 -Interval 1
ri.ps1 watch add -UI MainView -Path 'someField'
ri.ps1 watch list
ri.ps1 watch history -Id 1          # 加 -Full 输出每条记录的完整旧值 / 新值
ri.ps1 watch remove -Id 1
ri.ps1 watch clear
```

- `add` 以当前值为基线，返回监听项 `id` 和 `baseline`；之后每 `-Interval` 帧（默认 1）序列化一次路径值，与上次不同即记录一条
- 每条记录含 `seq`、`time`、`frame`、`changes`（`{"path","old","new"}`，只有 `old` 表示被移除，只有 `new` 表示新增），`changes` 中的 `path` 可直接用于 `get`
- 每个监听项只保留最近 30 条，`dropped` 为被丢弃的条数；路径暂时无法读取时记录一条 `error`
- `-Depth` 默认 3，超出深度的部分只比较摘要（如集合数量），需要细节时缩小路径；监听大模块、间隔 1 帧会拖慢运行
- 只能发现值变了，拿不到是谁改的；需要定位修改者时在可疑的 setter 或协议回调上打断点
- 退出 Play 时自动清空全部监听

## 事件派发

改数据（`set`）和派发事件是两个独立行为：`set` 只改内存不通知任何人；`event dispatch` 按给定参数走真实的事件回调，用来模拟服务器下发或客户端事件。

```powershell
ri.ps1 event list -Filter Assistant                     # 按类名关键字搜索事件
ri.ps1 event schema -Event PlayerAssistantDataEventArgs  # 参数结构与模板
ri.ps1 event dispatch -Event PlayerAssistantDataEventArgs -ValueFile args.json -Wait 30 -Watch 'BagDataModel,ui:MainView'
```

- `-Event`：事件参数类名（`XxxEventArgs`，可省略 `EventArgs` 后缀）、全名，或协议消息名（如 `XxxRsp`）；唯一包含匹配也可
- `list`：返回 `event`、`protocol`（是否为协议事件）、`listeners`（当前订阅数）
- `schema` 返回：
  - `listeners`：当前订阅者的类名和方法名
  - `types`：参数及嵌套类型的成员表；`enums`：枚举可选值
  - `template`：全部为默认值的 JSON 模板，对象列表附带一个元素模板仅用于展示结构，派发前按需填写或删除
- `dispatch`：
  - `-Value` / `-ValueFile` 为事件参数 JSON 对象，协议事件形如 `{"Message":{...}}`；**未给出的字段一律为默认值（0、空、空列表），不会沿用 Model 当前值**，需要哪些字段就给全
  - 参数从 ReferencePool 获取，经 `EventManager.DispatchEvent` 派发，与真实下发走同一条回调链
  - `-Wait` 派发后等待的帧数（默认 30，最多 300，最长 10 秒），覆盖下一帧刷新、界面异步加载等；Editor 暂停时不等待
  - `-Watch` 额外比较的根对象，逗号分隔，数据模块写名字、界面写 `ui:界面名`
  - `-Depth` 比较快照的序列化深度（默认 4）
- 响应报告：

| 字段 | 内容 |
|---|---|
| `listeners` | 处理该事件的回调（类名、方法名） |
| `exception` | 回调抛出的异常及堆栈；抛异常的回调之后的监听者不会执行 |
| `logs` | 等待期间的 Unity 日志，错误类附带堆栈，最多 100 条 |
| `modelChanges` | 每个比较对象的字段差异，格式同 `watch` 的 `changes`；默认比较订阅者所属的数据模块和界面 UIModel，加上 `-Watch` 指定的 |
| `unchanged` | 比较后无变化的对象 |
| `ui` | `opened` 新建、`destroyed` 销毁、`shown` 变为可见、`hidden` 变为隐藏的界面 |

**派发限制**（必须告知用户）：
- 回调里的全部副作用都会真实执行：弹窗、奖励展示、红点、回调里再向服务器发的请求等
- 等待期间其他逻辑产生的日志和数据变化也会计入报告；派发前界面尚未创建的 UIModel 不在比较范围内
- 模拟数据只存在于客户端，下次服务器同步会覆盖；同一时间只能有一个派发在等待

## 注意事项

- `-Props` 会执行普通属性的 getter，可能触发懒加载、改状态、重计算，默认不要开；需要计算属性的结果时再单独对该路径开启
- 返回 `409`：不在 Play 模式或 ModelManager 未创建；`504`：主线程被断点或弹窗阻塞，提醒用户恢复 Unity
- 无法连接：Unity 未打开或编译失败，让用户检查 Console，或执行菜单 `Tools/RuntimeInspector/重启服务`
- 返回的是读取瞬间的快照，异步流程中需在合适时机读取；需要跟踪过程时用 `watch`
- 能访问 ModelManager 中的数据模块和已创建界面的 UIModel；各类 Manager 和单例不在范围内


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
