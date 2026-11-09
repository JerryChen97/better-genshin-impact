using System;
using System.Text.Json;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPick;
using BetterGenshinImpact.ViewModel.Pages;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Script.Dependence;

public partial class AutoPathingScript
{
    /// <summary>在最终输入临界区核对目标、全局名单和本次路线/战后窗口身份。</summary>
    public bool TryPickup(string expectedName, string source, string token, bool artifactPrefix = false)
    {
        if (source is not ("global" or "point" or "mandatory")) return false;
        User32.VK key;
        try { key = KeyBindingsSettingsPageViewModel.MappingKey(User32.VK.VK_F); }
        catch (ArgumentException) { return false; }
        return GlobalPickupInputGuard.TryPickup(key, expectedName,
            source == "global", artifactPrefix, () =>
            {
                lock (_runGate)
                {
                    if (!_running || _pointLootSession is null ||
                        CancellationContext.Instance.Cts.IsCancellationRequested || RunnerContext.Instance.IsSuspend) return false;
                    if (source != "point") return token == _pointLootRouteSha256;
                    using var context = JsonDocument.Parse(_pointLootSession.GetContextJson());
                    return context.RootElement.ValueKind == JsonValueKind.Object &&
                           context.RootElement.GetProperty("token").GetString() == token;
                }
            });
    }
}
