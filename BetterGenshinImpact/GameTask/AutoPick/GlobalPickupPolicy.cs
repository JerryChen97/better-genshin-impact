using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoPick;

public static class GlobalPickupPolicy
{
    public static GlobalPickupRules Rules { get; } = new(Global.Absolute(""), reportError: error =>
        TaskControl.Logger.LogError(error, "全局拾取名单读取失败，暂停自动拾取直到名单恢复有效"));
}
