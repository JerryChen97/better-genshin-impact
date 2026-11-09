using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoPick;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Vanara.PInvoke;

public static class Probe
{
    public static string ScreenText = ""; public static BetterGenshinImpact.Core.Config.KeyId ActionKey = BetterGenshinImpact.Core.Config.KeyId.F; public static int Captures; public static Action<int>? AfterCapture; public static Action? AfterKeyDown; public static bool RouteActive = true; public static bool Suspended; public static string RouteHash = "route-sha"; public static string? PointToken = "point-token";
    public static bool PromptExists = true;
    public static bool IsMainUi = true;
    public static List<string> InputEvents = [];
    public sealed class Channel : InputChannelBase
    {
        protected override void OnKeyDown(User32.VK key) { InputEvents.Add("down:" + ScreenText); AfterKeyDown?.Invoke(); }
        protected override void OnKeyUp(User32.VK key) => InputEvents.Add("up:" + ScreenText);
        protected override void OnMouseButton(InputMouseButton button, bool down) { InputEvents.Add("mouse:" + button + ":" + down + ":" + ScreenText); }
        protected override void OnMoveBy(int dx, int dy) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
namespace Vanara.PInvoke
{
    public static class User32 { public enum VK { VK_F, VK_W, VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2 } }
}
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public interface ILogger<T> : ILogger { }
    public sealed class NullLogger<T> : ILogger<T> { }
    public static class LogExtensions
    {
        public static void LogError(this ILogger logger, Exception error, string message) { }
        public static void LogInformation(this ILogger logger, string message, params object[] values) { }
        public static void LogWarning(this ILogger logger, string message, params object[] values) { }
        public static void LogDebug(this ILogger logger, Exception error, string message, params object[] values) { }
    }
}
namespace BetterGenshinImpact
{
    public static class App { public static Microsoft.Extensions.Logging.ILogger<T> GetLogger<T>() => new Microsoft.Extensions.Logging.NullLogger<T>(); }
    public static class Global { public static string ReadAllTextIfExist(string path) => ""; }
}
namespace BetterGenshinImpact.Core.Config
{
    public enum KeyId { None, Unknown, F, W, X1, X2 }
    public static class KeyExtensions { public static User32.VK ToVK(this KeyId key) => key switch { KeyId.F => User32.VK.VK_F, KeyId.W => User32.VK.VK_W, KeyId.X1 => User32.VK.VK_XBUTTON1, KeyId.X2 => User32.VK.VK_XBUTTON2, _ => throw new ArgumentException("Unbound key") }; }
}
namespace BetterGenshinImpact.Core.Simulator.Extensions
{
    public enum GIActions { PickUpOrInteract, MoveForward }
    public enum KeyType { KeyDown, KeyUp, KeyPress, Hold }
    public static class ActionExtensions { public static BetterGenshinImpact.Core.Config.KeyId ToActionKey(this GIActions action) => action == GIActions.MoveForward ? BetterGenshinImpact.Core.Config.KeyId.W : Probe.ActionKey; }
}
namespace BetterGenshinImpact.Core.Input { public static class InputHub { public static IInputChannel Foreground = new Probe.Channel(); } }
namespace BetterGenshinImpact.Core.Script.Dependence.Model.TimerConfig
{
    public sealed class AutoPickExternalConfig { public bool ForceInteraction { get; set; } }
}
namespace BetterGenshinImpact.Helpers
{

    public static class UIDispatcherHelper { public static void BeginInvoke(Action action) { } }
    public sealed class SpeedTimer { public void Record(string label) { } public void DebugPrint() { } }
}
namespace BetterGenshinImpact.Service
{
    public static class ConfigService { public static System.Text.Json.JsonSerializerOptions JsonOptions = new(); }
}
namespace BetterGenshinImpact.View.Windows
{
    public static class ThemedMessageBox { public static void Error(string text) { } }
}
namespace BetterGenshinImpact.GameTask
{
    public interface ITaskTrigger { }
    public sealed class CaptureContent(string text) : IDisposable
    {
        public ImageRegion CaptureRectArea { get; } = new(text);
        public int FrameIndex => 1;
        public void Dispose() { }
    }
    public sealed class TaskContext
    {
        private static readonly TaskContext Current = new();
        public static TaskContext Instance() => Current;
        public bool IsInitialized { get; set; } = true;
        public ConfigData Config { get; } = new();
        public Info SystemInfo { get; } = new();
        public sealed class ConfigData { public AutoPickConfig AutoPickConfig { get; } = new(); }
        public sealed class Info { public double AssetScale => 1; }
    }
}
namespace BetterGenshinImpact.GameTask.AutoPick
{
    public enum AutoPickMode { Blacklist, Whitelist }
    public enum PickOcrEngineEnum { Yap }
    public sealed class AutoPickConfig
    {
        public bool Enabled => false;
        public AutoPickMode Mode { get; set; } = AutoPickMode.Blacklist;
        public bool BlacklistModePickEnabled => false;
        public bool WhitelistModeDoNotPickEnabled => false;
        public string PickKey { get; set; } = "F";
        public string OcrEngine => "Yap";
        public int ItemTextLeftOffset => 100;
        public int ItemTextRightOffset => 300;
        public int ItemIconLeftOffset => 20;
    }
    public static class GlobalPickupPolicy { public static GlobalPickupRules Rules = null!; }
}
namespace BetterGenshinImpact.GameTask.AutoPick.Assets
{
    public sealed class AutoPickAssets
    {
        public User32.VK PickVk => User32.VK.VK_F;
        public BetterGenshinImpact.Core.Recognition.RecognitionObject PickRo => new("F");
        public static AutoPickAssets Get(ImageRegion region, string key) => new();
    }
}
namespace BetterGenshinImpact.GameTask.Common
{
    public static class TaskControl
    {
        public static Microsoft.Extensions.Logging.ILogger Logger => BetterGenshinImpact.App.GetLogger<object>();
        public static ImageRegion CaptureToRectArea(bool forceNew = false) { var capture = new ImageRegion(Probe.ScreenText); var captureNumber = ++Probe.Captures; Probe.AfterCapture?.Invoke(captureNumber); return capture; }
    }
}
namespace BetterGenshinImpact.GameTask.Common.BgiVision
{
    public static class Bv { public static bool IsInMainUi(ImageRegion region) => Probe.IsMainUi; }
}
namespace BetterGenshinImpact.Core.Recognition
{
    public sealed class RecognitionObject(string name)
    {
        public string Name => name;
        public Rect RegionOfInterest { get; set; }
        public RecognitionObject Clone() => new(name);
    }
    public static class RecognitionAssets { public static RecognitionObject Get(string group, string name, ImageRegion region) => new(name); }
}
namespace BetterGenshinImpact.Core.Recognition.OCR
{
    public static class TextRectExtractor { public static Rect GetTextBoundingRect(Mat mat) => new(0, 0, 100, 10); }
    public static class OcrFactory { public static Service Paddle => new(); public sealed class Service { public string Ocr(Mat mat) => mat.Text; public string OcrWithoutDetector(Mat mat) => mat.Text; } }
}
namespace BetterGenshinImpact.Core.Recognition.ONNX.SVTR
{
    public static class TextInferenceFactory { public static Lazy<Service> Pick = new(() => new()); public sealed class Service { public string Inference(Mat mat) => mat.Text; } }
}
namespace BetterGenshinImpact.GameTask.Model.Area
{
    public class Region : IDisposable
    {
        public int X => 1090;
        public int Y => 400;
        public int Height => 30;
        public bool Empty { get; set; }
        public bool IsEmpty() => Empty;
        public bool IsExist() => !Empty;
        public void Dispose() { }
    }
    public sealed class ImageRegion(string text) : Region
    {
        public Mat SrcMat { get; } = new(text);
        public Mat CacheGreyMat => SrcMat;
        public Region Find(BetterGenshinImpact.Core.Recognition.RecognitionObject ro) => new() { Empty = ro.Name != "F" || !Probe.PromptExists };
    }
}
namespace OpenCvSharp
{
    public readonly struct Rect(int x, int y, int width, int height)
    {
        public int X => x; public int Y => y; public int Width => width; public int Height => height;
        public int Right => x + width; public int Bottom => y + height;
    }
    public readonly struct Size(int width, int height) { }
    public readonly struct Scalar(double value)
    {
        public double Val0 => value;
        public Scalar(double a, double b, double c) : this(a) { }
        public static Scalar FromRgb(int a, int b, int c) => new(a);
    }
    public readonly struct Vec3b { public byte Item0 => 0; public byte Item1 => 0; public byte Item2 => 0; }
    public enum MatType { CV_32F }
    public enum MorphShapes { Rect }
    public enum MorphTypes { Open }
    public sealed class Mat : IDisposable
    {
        public string Text { get; } = "";
        public int Width => 1920;
        public int Height => 1080;
        public Mat() { }
        public Mat(string text) { Text = text; }
        public Mat(Mat source, Rect rect) { Text = source.Text; }
        public Mat Sobel(MatType type, int a, int b) => this;
        public Scalar Mean() => new(0);
        public T At<T>(int x, int y) where T : struct => default;
        public void Dispose() { }
    }
    public static class Cv2
    {
        public static void InRange(Mat a, Scalar b, Scalar c, Mat d) { }
        public static Mat GetStructuringElement(MorphShapes shape, Size size) => new();
        public static void MorphologyEx(Mat a, Mat b, MorphTypes c, Mat d, int iterations) { }
        public static void Dilate(Mat a, Mat b, Mat c, int iterations) { }
        public static Rect BoundingRect(Mat mat) => new(0, 0, 1, 1);
    }
}
