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
        public static void LogDebug(this object logger, Exception e, string text) { }
        public static void LogError(this object logger, string text, string message) { }
        public static void LogInformation(this object logger, string text) { }
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
    public sealed class Capture : IDisposable { public void Dispose() { } }
    public sealed class CancellationContext
    {
        public static CancellationContext Instance { get; } = new();
        public CancellationTokenSource Cts { get; set; } = new();
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
        public Func<CancellationToken, Task>? BeforeGetScenes;
        public async Task<AutoFight.Model.CombatScenes?> GetCombatScenes(CancellationToken ct)
        {
            if (BeforeGetScenes is not null) await BeforeGetScenes(ct);
            return Scenes;
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
}
namespace BetterGenshinImpact.Core.Input
{
    public static class InputHub { public static int ReleaseCalls; public static void ReleaseAll() { ReleaseCalls++; } }
}
namespace BetterGenshinImpact.GameTask.Common.BgiVision
{
    public static class Bv
    {
        public static bool MainUi = true;
        public static bool RevivePrompt;
        public static Queue<bool> MainUiFrames { get; } = new();
        public static int MainUiReads;
        public static bool IsInMainUi(object capture)
        {
            MainUiReads++;
            return MainUiFrames.Count > 0 ? MainUiFrames.Dequeue() : MainUi;
        }
        public static bool IsInRevivePrompt(object capture) => RevivePrompt;
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
        public Task Pathing(PathingTask task) => Execute(this, task, token);
        internal void ClearPointLootRun() { Cleared++; PointLootSession?.Revoke(); }
    }
}
