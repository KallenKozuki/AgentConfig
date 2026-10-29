# 新活动文件模板

以 `Xxx` 代指活动名。模板提炼自 `GroupBuy` 与 `WestOcean`，按实际协议删改。注释遵守 C# 注释规范：public 用 XML 注释，private 用 `//`，结尾不加句号。

## XxxDataModel.cs

```csharp
using YQFramework.Protocol;

namespace YQFramework.Model
{
    /// <summary>
    /// Xxx 活动数据模型
    /// </summary>
    public partial class XxxDataModel : ModelBase
    {
        private int redDotResetTimerId;

        /// <summary>
        /// 当前缓存对应的活动 Id
        /// </summary>
        public int ActivityId { get; private set; }

        /// <summary>
        /// 玩家活动数据
        /// </summary>
        public XxxUserData UserData { get; private set; }

        /// <summary>
        /// 初始化数据模型
        /// </summary>
        public override void Init()
        {
            AddEventListener();
        }

        /// <summary>
        /// 清理数据模型缓存
        /// </summary>
        public override void Clear()
        {
            ClearData();
        }

        // 切换活动时清除上一期缓存
        private void PrepareActivity(int activityId)
        {
            if (activityId <= 0 || activityId == ActivityId)
                return;

            ClearData();
            ActivityId = activityId;
        }

        // 清理全部缓存、红点与定时器
        private void ClearData()
        {
            ClearRedDots();
            TimerManager.Instance?.StopTimer(redDotResetTimerId);
            redDotResetTimerId = 0;
            ActivityId = 0;
            UserData = null;
        }

        // 判断回包是否属于当前活动
        private bool IsCurrentActivityResponse(int activityId)
        {
            return ActivityId > 0 && ActivityId == activityId;
        }
    }
}
```

## XxxDataModel.Event.cs

```csharp
using YQFramework.Event;
using YQFramework.FUI;
using YQFramework.Network;
using YQFramework.Pool.ReferencePool;
using YQFramework.Protocol;

namespace YQFramework.Model
{
    public partial class XxxDataModel
    {
        // 注册协议与红点相关事件
        private void AddEventListener()
        {
            EventManager.Instance.AddListener(PlayerXxxDataEventArgs.EventId, OnPlayerXxxDataEvent);
            EventManager.Instance.AddListener(BagItemUpdatedEventArgs.EventId, OnBagItemUpdatedEvent);
        }

        #region 请求
        /// <summary>
        /// 请求玩家活动数据
        /// </summary>
        /// <param name="activityId">活动 Id</param>
        public void RequestXxxData(int activityId)
        {
            if (activityId <= 0)
                return;

            PrepareActivity(activityId);
            PlayerXxxDataReq req = ReferencePool.Acquire<PlayerXxxDataReq>();
            req.PlayerId = ModelManager.Instance.GetModel<UserDataModel>().PlayerId;
            req.ActivityId = activityId;
            NetworkManager.Instance.Send(req);
            ReferencePool.Release(req);
        }
        #endregion

        #region 回包
        // 处理玩家活动数据回包
        private void OnPlayerXxxDataEvent(object sender, GameEventArgs e)
        {
            PlayerXxxDataEventArgs args = e as PlayerXxxDataEventArgs;
            if (args.Message.Ret != (int)ErrorCodeEnum.ErrNone)
            {
                UIHelper.ShowErrorTip(args.Message.Ret);
                return;
            }

            if (!IsCurrentActivityResponse(args.Message.ActivityId))
                return;

            UserData = args.Message.Data;
            RefreshRedDots();
            DispatchDataChanged();
        }

        // 通知 UI 活动数据变更
        private void DispatchDataChanged()
        {
            EventManager.Instance.DispatchEvent(
                XxxDataChangedEventArgs.EventId,
                this,
                XxxDataChangedEventArgs.Create(ActivityId));
        }
        #endregion
    }
}
```

## XxxDataModel.RedDot.cs

```csharp
using System.Linq;
using YQFramework.Event;
using YQFramework.RedDot;

namespace YQFramework.Model
{
    public partial class XxxDataModel
    {
        /// <summary>
        /// 刷新全部活动红点
        /// </summary>
        public void RefreshRedDots()
        {
            RefreshOpenRedDot();
            RefreshRewardRedDot();
        }

        /// <summary>
        /// 标记活动入口已查看
        /// </summary>
        public void MarkOpenViewed()
        {
            if (ActivityId <= 0)
                return;

            ModelManager.Instance.GetModel<UserDataModel>().SetInt($"{Const.XXX_OPEN_RED_DOT_VIEWED_KEY_PREFIX}{ActivityId}", 1);
            RefreshOpenRedDot();
        }

        // 清零全部活动红点，包含动态子节点
        private void ClearRedDots()
        {
            if (ActivityId <= 0)
                return;

            SetRedDot(RedDotName.XxxOpen, false);
            SetRedDot(RedDotName.XxxReward, false);
        }

        // 刷新首次进入活动红点
        private void RefreshOpenRedDot()
        {
            bool isViewed = ModelManager.Instance.GetModel<UserDataModel>()
                .GetInt($"{Const.XXX_OPEN_RED_DOT_VIEWED_KEY_PREFIX}{ActivityId}", 0) == 1;
            SetRedDot(RedDotName.XxxOpen, ActivityHelper.IsOpen(ActivityId) && !isViewed);
        }

        // 刷新奖励可领取红点
        private void RefreshRewardRedDot()
        {
            bool canClaim = ActivityHelper.IsOpen(ActivityId) && UserData?.CanClaim == true;
            SetRedDot(RedDotName.XxxReward, canClaim);
        }

        // 背包变更时只刷新相关道具的红点
        private void OnBagItemUpdatedEvent(object sender, GameEventArgs args)
        {
            BagItemUpdatedEventArgs e = args as BagItemUpdatedEventArgs;
            if (ActivityId <= 0 || e.Items == null || !e.Items.Contains(XxxHelper.XXX_COST_ITEM_ID))
                return;

            RefreshRewardRedDot();
        }

        // 写入活动红点值
        private void SetRedDot(string format, bool value)
        {
            if (ActivityId <= 0)
                return;

            string redDotName = ActivityHelper.GetRedDotName(ActivityId, format);
            if (!string.IsNullOrEmpty(redDotName))
                RedDotManager.Instance.ChangeValue(redDotName, value ? 1 : 0);
        }
    }
}
```

## XxxHelper.Config.cs

```csharp
using YQFramework.Config;

namespace YQFramework.Model
{
    public static partial class XxxHelper
    {
        private static int _XXX_COST_ITEM_ID;
        private static int[] _XXX_OPEN_TIME;

        /// <summary>
        /// 消耗道具 Id，配置缺失时为 0
        /// </summary>
        public static int XXX_COST_ITEM_ID
        {
            get
            {
                if (_XXX_COST_ITEM_ID == 0)
                    _XXX_COST_ITEM_ID = ConfigHelper.ParseActParamConfig(XxxActivityId, "XXX_COST_ITEM_ID");

                return _XXX_COST_ITEM_ID;
            }
        }

        /// <summary>
        /// 每日开放时间，格式为开始小时|结束小时，配置缺失时为 null
        /// </summary>
        public static int[] XXX_OPEN_TIME
        {
            get
            {
                if (_XXX_OPEN_TIME != null)
                    return _XXX_OPEN_TIME;

                int[] openTime = ConfigHelper.ParseActParamConfigIntArray(XxxActivityId, "XXX_OPEN_TIME");
                if (openTime?.Length >= 2)
                    _XXX_OPEN_TIME = openTime;

                return openTime;
            }
        }
    }
}
```

`XxxActivityId` 定义在 `XxxHelper.cs`：`public static int XxxActivityId => ModelManager.Instance.GetModel<XxxDataModel>().ActivityId;`

## EventArgs/Xxx/XxxDataChangedEventArgs.cs

```csharp
using YQFramework.Pool.ReferencePool;

namespace YQFramework.Event
{
    /// <summary>
    /// Xxx 活动数据变更事件
    /// </summary>
    public class XxxDataChangedEventArgs : GameEventArgs
    {
        /// <summary>
        /// 当前活动 Id
        /// </summary>
        public int ActivityId;

        /// <summary>
        /// 创建数据变更事件
        /// </summary>
        /// <param name="activityId">活动 Id</param>
        /// <returns>数据变更事件</returns>
        public static XxxDataChangedEventArgs Create(int activityId)
        {
            XxxDataChangedEventArgs args = ReferencePool.Acquire<XxxDataChangedEventArgs>();
            args.ActivityId = activityId;
            return args;
        }

        /// <summary>
        /// 清理事件参数
        /// </summary>
        public override void Clear()
        {
            ActivityId = 0;
        }

        /// <summary>
        /// 数据变更事件标识
        /// </summary>
        public static readonly string EventId = typeof(XxxDataChangedEventArgs).FullName;
    }
}
```
