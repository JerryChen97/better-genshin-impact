namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>普通拾取未启用时仍识别全局白名单；保留调度器的暂停和界面保护。</summary>
public sealed class GlobalPickupTrigger : ITaskTrigger
{
    private readonly AutoPickTrigger _picker = new(globalOnly: true);
    public string Name => "全局白名单拾取";
    public int Priority => 30;
    public bool IsExclusive => false;
    public bool IsEnabledByConfig => GlobalPickupPolicy.Rules.GetSnapshot().HasWhitelist;
    public void OnEnabled(object? options) => _picker.OnEnabled(null);
    public void OnDisabled() => _picker.OnDisabled();
    public void OnCapture(CaptureContent content) => _picker.OnCapture(content);
}
