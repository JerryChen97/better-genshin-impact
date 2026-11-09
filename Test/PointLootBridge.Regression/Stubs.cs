// Only platform/game boundaries are stubbed. The session and public script bridge are linked production files.
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact
{
    public static class Global { public static string Absolute(string path) => path; }
}
namespace BetterGenshinImpact.Core.Config { public class PathingPartyConfig { } }
namespace Microsoft.Extensions.Logging
{
    public static class LoggingExtensions
    {
        public static readonly List<(Exception Error, string Text)> Debugs = new();
        public static void LogDebug(this object logger, Exception e, string text) { Debugs.Add((e, text)); }
        public static void LogError(this object logger, string text, string message) { }
        public static void LogInformation(this object logger, string text) { }
        public static readonly List<string> Warnings = new();
        public static void LogWarning(this object logger, string text, string message) { Warnings.Add(message); }
        public static void LogWarning(this object logger, string text) { Warnings.Add(text); }
    }
}
namespace BetterGenshinImpact.GameTask.Common
{
    public static class TaskControl
    {
        public static object Logger { get; } = new();
        public static int HoldCalls;
        public static int PlungeCalls;
        public static Action? BeforeDelay;
        public static Capture CaptureToRectArea() => new();
        public static Task Delay(int ms, CancellationToken ct)
        {
            BeforeDelay?.Invoke();
            if (ct.IsCancellationRequested) throw new AutoGeniusInvokation.Exception.NormalEndException("Cancelled at Delay entry");
            return Task.CompletedTask;
        }
        public static Task SimulateHoldElementalSkillAsync(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); HoldCalls++; return Task.CompletedTask; }
        public static Task SimulateMouseLeftClickLoopAsync(int count, CancellationToken ct) { ct.ThrowIfCancellationRequested(); PlungeCalls++; return Task.CompletedTask; }
    }
    public sealed class Capture : BetterGenshinImpact.GameTask.Model.Area.ImageRegion { }
    public sealed class CancellationContext
    {
        public static CancellationContext Instance { get; } = new();
        public CancellationTokenSource Cts { get; set; } = new();
    }
}
namespace BetterGenshinImpact.Core.Recognition
{
    public sealed class RecognitionObject { }
}
namespace BetterGenshinImpact.GameTask.Model.Area
{
    public class ImageRegion : IDisposable
    {
        public static bool MapButtonExists = true;
        public FoundRegion Find(BetterGenshinImpact.Core.Recognition.RecognitionObject recognition) => new();
        public void Dispose() { }
    }
    public sealed class FoundRegion : IDisposable
    {
        public bool IsExist() => ImageRegion.MapButtonExists;
        public bool IsEmpty() => !IsExist();
        public void Dispose() { }
    }
}
namespace BetterGenshinImpact.Core.Script.Dependence
{
    public sealed class LimitedFile
    {
        public LimitedFile(string root) { }
        public Task<string> ReadText(string path) => Task.FromResult(File.ReadAllText(path));
        public bool IsExists(string path) => File.Exists(path);
        public bool IsFile(string path) => File.Exists(path);
        public bool IsFolder(string path) => Directory.Exists(path);
        public string[] ReadPathSync(string path) => [];
        public string ReadTextSync(string path) => File.ReadAllText(path);
    }
}
namespace BetterGenshinImpact.GameTask.AutoPathing.Model.Enum
{
    public sealed record ActionEnum(string Code) { public static ActionEnum Fight { get; } = new("fight"); }
}
namespace BetterGenshinImpact.GameTask.AutoPathing.Model
{
    public sealed record Waypoint(string Action);
    public sealed class PathingTask
    {
        public List<Waypoint> Positions { get; set; } = [];
        public static PathingTask BuildFromJson(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<PathingTask>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
namespace BetterGenshinImpact.GameTask
{
    public sealed class RunnerContext
    {
        public static RunnerContext Instance { get; } = new();
        public int StopCount { get; private set; }
        public AutoFight.Model.CombatScenes Scenes { get; } = new();
        public bool ScenesMissing; // Production returns null when the party cannot be recognized.
        public Func<CancellationToken, Task>? BeforeGetScenes;
        public async Task<AutoFight.Model.CombatScenes?> GetCombatScenes(CancellationToken ct)
        {
            if (BeforeGetScenes is not null) await BeforeGetScenes(ct);
            return ScenesMissing ? null : Scenes;
        }
        public async Task StopAutoPickRunTask(Func<Task> task)
        {
            StopCount++;
            try { await task(); } finally { StopCount--; }
        }
    }
}
namespace BetterGenshinImpact.GameTask.AutoFight.Model
{
    public sealed class CombatScenes
    {
        public FakeAvatar Avatar { get; } = new();
        public bool KazuhaPresent = true;
        public CancellationToken Token;
        public FakeAvatar? SelectAvatar(string name) => name == "枫原万叶" && KazuhaPresent ? Avatar : null;
        public void BeforeTask(CancellationToken token) { Token = token; }
    }
    public sealed class FakeAvatar
    {
        public Func<CancellationToken, Task> Wait { get; set; } = _ => Task.CompletedTask;
        public Func<bool>? Switch;
        public Action<CancellationToken>? RecoverSwitch;
        public bool TrySwitch(int count, CancellationToken? recoveryCt = null)
        {
            if (Switch is not null) return Switch();
            if (RunnerContext.Instance.Scenes.Token.IsCancellationRequested) return false;
            RecoverSwitch?.Invoke(recoveryCt ?? RunnerContext.Instance.Scenes.Token);
            return true;
        }
        public Task WaitSkillCd(CancellationToken token) => Wait(token);
        public void AfterUseSkill() { }
    }
    // Production Avatar.RecoverFromRevivePrompt closes the prompt, recovers at the statue and always throws. Inside a
    // point-policy scope: the verified revive retry after a completed recovery with a confirmed main UI, or after a
    // restored statue-teleport timeout; InvalidOperationException for an unconfirmed main UI; a stop surfaces as
    // NormalEndException before the statue recovery starts and as OperationCanceledException inside it; other recovery
    // failures propagate unchanged. The stub models the completed recovery.
    public static class Avatar
    {
        public static int RecoverCalls;
        public static CancellationToken RecoverToken;
        public static void RecoverFromRevivePrompt(CancellationToken ct)
        {
            RecoverCalls++;
            RecoverToken = ct;
            var retry = new AutoGeniusInvokation.Exception.RetryException("检测到复苏界面，存在角色被击败，前往七天神像复活");
            if (PointLootRecoveryScope.IsActive)
                PointLootRecoveryScope.RecoverThenRetry(() => { }, () => true, ct, defeated: true, retry);
            throw retry;
        }
    }
}
namespace BetterGenshinImpact.Core.Input
{
    public static class InputHub
    {
        public static int ReleaseCalls;
        public static void ReleaseAll() { ReleaseCalls++; }
        public static FakeForeground Foreground { get; } = new();
    }
    public sealed class FakeForeground { public FakeKeyboard Keyboard { get; } = new(); }
    public sealed class FakeKeyboard
    {
        public List<Vanara.PInvoke.User32.VK> Presses { get; } = new();
        public Action? BeforePress;
        public void KeyPress(Vanara.PInvoke.User32.VK key) { BeforePress?.Invoke(); Presses.Add(key); }
    }
}
namespace Vanara.PInvoke
{
    public static class User32 { public enum VK { VK_ESCAPE } }
}
namespace BetterGenshinImpact.GameTask.Common.BgiVision
{
    public static class Bv
    {
        public static bool MainUi = true;
        public static bool RevivePrompt;
        public static bool BigMapUi;
        public static Func<bool>? OnMapRead;
        public static bool IsInBigMapUi(object capture) => OnMapRead?.Invoke() ?? BigMapUi;
        public static Queue<bool> MainUiFrames { get; } = new();
        public static int MainUiReads;
        public static Action<int>? OnRead;
        public static bool IsInMainUi(object capture)
        {
            MainUiReads++;
            OnRead?.Invoke(MainUiReads);
            return MainUiFrames.Count > 0 ? MainUiFrames.Dequeue() : MainUi;
        }
        public static bool IsInRevivePrompt(object capture) => RevivePrompt;
        public static bool LowHp;
        public static bool CurrentAvatarIsLowHp(object capture) => LowHp;
    }
}
namespace BetterGenshinImpact.GameTask.Common.Job
{
    // Production ReturnMainUiTask presses ESC until the main UI shows; the stub only counts rounds.
    public sealed class ReturnMainUiTask
    {
        public static int Starts;
        public static Action<CancellationToken>? OnStart;
        public Task Start(CancellationToken ct) { Starts++; OnStart?.Invoke(ct); return Task.CompletedTask; }
    }
}
namespace BetterGenshinImpact.GameTask.AutoPathing
{
    public sealed class PathExecutor(CancellationToken token)
    {
        public static Func<PathExecutor, PathingTask, CancellationToken, Task> Execute { get; set; } = (_, _, _) => Task.CompletedTask;
        public static int Created;
        public static int Cleared;
        private readonly int _id = Interlocked.Increment(ref Created);
        internal PointLootSession? PointLootSession { get; set; }
        public BetterGenshinImpact.Core.Config.PathingPartyConfig? PartyConfig { get; set; }
        public bool SuccessEnd;
        internal bool PointSkipEnabled { get; set; }
        internal PointLootTeleportRecovery? TeleportRecovery { get; set; }
        public readonly HashSet<int> Completed = new();
        public readonly List<(int OriginalIndex, string Code)> Skipped = new();
        internal IReadOnlyCollection<int> CompletedFightPoints => Completed;
        internal IReadOnlyList<(int OriginalIndex, string Code)> SkippedFightPoints => Skipped;
        public Task Pathing(PathingTask task) => Execute(this, task, token);
        internal void ClearPointLootRun() { Cleared++; PointLootSession?.Revoke(); }
    }
}
