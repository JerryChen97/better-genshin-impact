// 只替换外部输入/日志与识图边界；输入通道本身链接生产源码，测试不会发送真实按键。
namespace Vanara.PInvoke
{
    public static class User32
    {
        public enum VK { VK_F, VK_W, VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2 }
    }
}
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public sealed class NullLogger : ILogger { }
    public static class LogExtensions
    {
        public static void LogWarning(this ILogger logger, string message, params object[] values) { }
        public static void LogDebug(this ILogger logger, Exception error, string message, params object[] values) { }
    }
}
namespace BetterGenshinImpact
{
    public static class App
    {
        public static Microsoft.Extensions.Logging.ILogger GetLogger<T>() => new Microsoft.Extensions.Logging.NullLogger();
    }
}
namespace BetterGenshinImpact.Core.Config
{
    public enum KeyId { None, Unknown, F, W }
    public static class KeyExtensions
    {
        public static Vanara.PInvoke.User32.VK ToVK(this KeyId key) =>
            key == KeyId.F ? Vanara.PInvoke.User32.VK.VK_F : Vanara.PInvoke.User32.VK.VK_W;
    }
}
namespace BetterGenshinImpact.Core.Simulator.Extensions
{
    public enum GIActions { PickUpOrInteract, MoveForward }
    public enum KeyType { KeyDown, KeyUp, KeyPress, Hold }
    public static class ActionExtensions
    {
        public static BetterGenshinImpact.Core.Config.KeyId ToActionKey(this GIActions action) =>
            action == GIActions.PickUpOrInteract ? BetterGenshinImpact.Core.Config.KeyId.F : BetterGenshinImpact.Core.Config.KeyId.W;
    }
}
namespace BetterGenshinImpact.GameTask.AutoPick
{
    public static class GlobalPickupInputGuard
    {
        public enum Result { Pass, Block, Pulse }
        public static IDisposable? Enter(Vanara.PInvoke.User32.VK key) => null;
        public static Result PickupResult { get; set; }
        public static Result Check(Vanara.PInvoke.User32.VK key) =>
            key == Vanara.PInvoke.User32.VK.VK_F ? PickupResult : Result.Pass;
    }
}
namespace GlobalPickupRegression
{
    public sealed class ProbeChannel : BetterGenshinImpact.Core.Input.InputChannelBase
    {
        public List<string> Events { get; } = [];
        protected override void OnKeyDown(Vanara.PInvoke.User32.VK key) => Events.Add("down:" + key);
        protected override void OnKeyUp(Vanara.PInvoke.User32.VK key) => Events.Add("up:" + key);
        protected override void OnMouseButton(BetterGenshinImpact.Core.Input.InputMouseButton button, bool down) => Events.Add("mouse:" + button + ":" + down);
        protected override void OnMoveBy(int dx, int dy) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
