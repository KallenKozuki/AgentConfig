---
name: reddot-integration
description: 为非活动类系统接入红点：定义 RedDotName 路径、编写 XxxDataModel.RedDot.cs、选择刷新时机、在 UICtrl 中挂载红点。用于新增系统红点、红点接入、红点不刷新、红点消不掉、入口红点与内部红点不一致、切换账号后红点残留等场景。活动红点见 new-activity-scaffold。
---

# 系统红点接入骨架

适用于常驻系统（小助手、门客、公会等）。活动红点挂在 `ActivityRoot` 下，规则见 `new-activity-scaffold`，本文的通用机制同样适用。

- 标准样板：`Model/Assistant/AssistantDataModel.RedDot.cs`、`UILogic/Assistant/AssistantMain/AssistantMainCtrl.RedDot.cs`
- 旧写法参考（不要照抄）：`PartnerDataModel.RedDot.cs`（空 `catch` 吞异常）、`GuildDataModel.RedDot.cs`（未解锁时直接 `return`，不清零）

## 红点树机制

- 红点是按 `/` 分隔的路径树，节点在首次使用时自动创建，父节点由子节点自动汇总
- **叶子节点名决定样式**，通过 `Const.RED_DOT_STYLE_DICT` 映射：`NormalRedDot` 为普通红点，`NumberRedDot` 为数字红点，其余见 `RedDotStyle`
- 父节点显示子节点中优先级最高的样式（`Const.RedDotPriority`）
- 只写叶子节点：`RedDotManager.Instance.ChangeValue(path, value)`；入口只读或只挂载根节点

## 接入步骤

```
- [ ] 1. RedDotName 定义路径
- [ ] 2. 新建 XxxDataModel.RedDot.cs，按骨架实现
- [ ] 3. Init 中注册红点事件，数据回包处调用对应 Refresh
- [ ] 4. ClearData 中调用 ClearRedDots
- [ ] 5. UICtrl.RedDot.cs 挂载红点
- [ ] 6. 按自检清单逐项核对
```

### 1. 定义路径

在 `Assets/Scripts/GamePlay/Reddot/RedDotName.cs` 中按系统建 `#region`，每行写行尾注释：

- 根节点：`XxxRoot = "XxxRoot"`，供入口挂载和监听
- 叶子节点：以样式名结尾，例如 `"XxxRoot/Unlock/NormalRedDot"`
- 动态节点：用 `{0}` 占位，使用时 `RedDotName.XxxItem.Fmt(id)`；同时定义动态节点的父路径（如 `"XxxRoot/SkinUnlock"`），供页签或按钮挂载

### 2. DataModel 红点分部类骨架

```csharp
public partial class XxxDataModel
{
    // 注册红点相关事件
    private void AddRedDotEventListeners()
    {
        EventManager.Instance.AddListener(SystemOpenEventArgs.EventId, OnSystemOpenEvent);
        EventManager.Instance.AddListener(BagItemUpdatedEventArgs.EventId, OnBagItemUpdatedEvent);
    }

    /// <summary>
    /// 刷新全部红点
    /// </summary>
    internal void RefreshRedDots()
    {
        RefreshUnlockRedDot();
        RefreshItemRedDots();
    }

    /// <summary>
    /// 清理全部红点，包含动态子节点
    /// </summary>
    internal void ClearRedDots()
    {
        RedDotManager.Instance.ChangeValue(RedDotName.XxxUnlock, 0);
        foreach (XxxCfg cfg in TableCfgs.TbXxx.DataList)
            RedDotManager.Instance.ChangeValue(RedDotName.XxxItem.Fmt(cfg.Id), 0);
    }

    // 刷新单项红点：任何分支都写入 0 或 1
    private void RefreshUnlockRedDot()
    {
        bool isOpen = SystemOpenHelper.IsOpen(GameSystemType.Xxx);
        RedDotManager.Instance.ChangeValue(RedDotName.XxxUnlock, isOpen && !IsUnlockViewed() ? 1 : 0);
    }

    // 系统解锁完成
    private void OnSystemOpenEvent(object sender, GameEventArgs args)
    {
        SystemOpenEventArgs e = (SystemOpenEventArgs)args;
        if (e.FunctionIdList.Contains((int)GameSystemType.Xxx))
            RefreshRedDots();
    }
}
```

要点：

- 每个 `Refresh*RedDot` 只负责一个节点或一组同类动态节点，**每个分支都要写值**；未解锁、不在公会等前置条件不满足时写 0，不能直接 `return`
- `RefreshRedDots` 汇总调用；数据回包只调用受影响的单项刷新
- `ClearRedDots` 必须覆盖动态子节点，并在 `ClearData` 中调用（`Model.Clear` 会在切换账号或重登时执行）
- 动态节点集合变化（例如目标换了一批 id）时，先把旧集合清零再写新值，参考 `WestOceanDataModel.ClearPromotionLeafRedDots`
- 不要用空 `catch` 吞异常；确需兜底时使用 `LogManager.Instance.LogError`

### 3. 刷新时机

| 数据变化 | 监听 | 处理 |
|---|---|---|
| 系统解锁 | `SystemOpenEventArgs` | `FunctionIdList` 命中本系统时调用 `RefreshRedDots` |
| 背包道具 | `BagItemUpdatedEventArgs` | 先用 `e.Items` 过滤相关道具 id，再刷新对应项；消耗 id 集合可懒加载缓存 |
| 协议数据 | 本系统协议回包 | 写入数据后立即刷新受影响的红点 |
| 跨天 | `ServerCrossDayEventArgs` | 重置每日相关红点，必要时重新请求数据 |
| 持久化标记同步 | `PlayerClientMarkDataEventArgs` | 刷新依赖 `PersistentDataModel` 的查看型红点 |
| 其他系统数据 | 对方的数据变更事件 | 不要在 UI 中补刷新，红点归属方监听事件后自行刷新 |

### 4. 查看型红点（首次解锁、新内容）

- 需要跨设备保留时使用 `PersistentDataModel`：在 `Model/Persistent/Enum.Persisitent.cs` 新增 `PersistentType`，`SetInt` / `GetInt`；同步回包后需要刷新
- 仅本机记录时使用 `UserDataModel.SetInt(key, 1)`，key 前缀定义在 `Const.Xxx.cs`
- 提供 `MarkXxxViewed()`：先检查系统已解锁，写入标记后调用对应刷新；由界面打开或点击时调用

### 5. UICtrl 挂载

在 `XxxCtrl.RedDot.cs` 分部类中实现（Ctrl 不声明字段，遵守 UI Controller 结构规范）：

```csharp
// 注册红点监听
public override void AddRedDotListener()
{
    BindRedDotListener(RedDotName.XxxSkinUnlock, OnXxxSkinUnlockRedDotChanged);
}

// 刷新外观按钮红点
private void RefreshXxxSkinUnlockRedDot()
{
    RedDotManager.Instance.RefreshRedDot(RedDotName.XxxSkinUnlock, view.SkinButton, new(0, 130, 0));
}

// 外观红点变化
private void OnXxxSkinUnlockRedDotChanged(TreeNode treeNode)
{
    if (!view.visible)
        return;

    RefreshXxxSkinUnlockRedDot();
}
```

- `BindRedDotListener` 的监听由基类在界面关闭时统一移除，不要手动移除
- `OnShow` 中主动刷新一次；列表项在渲染回调中按动态路径调用 `RefreshRedDot`
- 主界面入口挂载系统根节点（参考 `MainCtrl.Assistant.cs` 中的 `AssistantRoot`），**不要为入口单独写一套判断**，否则会出现入口和内部红点不一致
- UI 中只挂载和读取红点，不计算红点条件

## 自检清单

- [ ] 叶子节点以样式名结尾，入口挂载的是父节点
- [ ] 每个刷新方法的所有分支都会写值，未解锁时写 0
- [ ] 系统解锁、道具变化、协议回包、跨天都有对应刷新
- [ ] `ClearRedDots` 覆盖全部节点（含动态节点），并在 `Clear` 链路中调用
- [ ] 查看型红点的存储方式（`PersistentDataModel` 或 `UserDataModel`）已确认
- [ ] UI 不计算红点条件，入口与内部共用同一棵树


## 全局安装路径约定

- 本技能中的 Assets、Library、ProjectSettings 及 ../proto 路径均相对于当前 Unity 项目根目录，不相对于技能安装目录；执行项目工具前先切换至目标项目根目录。
