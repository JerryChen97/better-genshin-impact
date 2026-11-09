namespace BetterGenshinImpact.GameTask
{
    public sealed class CancellationContext
    {
        public static CancellationContext Instance { get; } = new();
        public System.Threading.CancellationTokenSource Cts { get; set; } = new();
    }
    public sealed class RunnerContext
    {
        public static RunnerContext Instance { get; } = new();
        public bool IsSuspend => Probe.Suspended;
    }
}
namespace BetterGenshinImpact.ViewModel.Pages
{
    public static class KeyBindingsSettingsPageViewModel
    {
        public static Vanara.PInvoke.User32.VK MappingKey(Vanara.PInvoke.User32.VK key) =>
            BetterGenshinImpact.Core.Config.KeyExtensions.ToVK(Probe.ActionKey);
    }
}
namespace BetterGenshinImpact.Core.Script.Dependence
{
    // 路线状态由现有桥接回归覆盖；此处只替换最终 API 的生命周期边界。
    public partial class AutoPathingScript
    {
        private readonly object _runGate = new();
        private bool _running => Probe.RouteActive;
        private readonly FakeSession _pointLootSession = new();
        private string _pointLootRouteSha256 => Probe.RouteHash;
        private sealed class FakeSession
        {
            public string GetContextJson() => Probe.PointToken is null ? "null" :
                System.Text.Json.JsonSerializer.Serialize(new { token = Probe.PointToken });
        }
    }
}
