// Only the platform, logging and command/game boundaries are stubbed.
// The shared executor, pathing handler and recovery classifier are linked production files.
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public sealed class TestLogger : ILogger { }
    public static class LoggingExtensions
    {
        public static void LogError(this ILogger logger, string message, params object[] args) { }
        public static void LogError(this ILogger logger, Exception error, string message) { }
        public static void LogWarning(this ILogger logger, string message, params object[] args) { }
        public static void LogWarning(this ILogger logger, Exception error, string message) { }
        public static void LogInformation(this ILogger logger, string message, params object[] args) { }
    }
}
namespace BetterGenshinImpact.Core.Config { public sealed class PathingPartyConfig { } }
namespace BetterGenshinImpact.GameTask.Common
{
    public static class TaskControl
    {
        public static Microsoft.Extensions.Logging.ILogger Logger { get; } = new Microsoft.Extensions.Logging.TestLogger();
        public static Capture CaptureToRectArea() => new();
    }
    public sealed class Capture : IDisposable
    {
        public static int Disposed;
        public void Dispose() => Disposed++;
    }
}
namespace BetterGenshinImpact.GameTask.AutoFight.Model
{
    public sealed class Avatar { public string Name { get; } = "测试角色"; }
    public sealed class CombatScenes : IDisposable
    {
        public static int Disposed;
        public static Exception? DisposeError;
        public CancellationToken Token;
        public void InitializeTeam(object capture) { }
        public bool CheckTeamInitialized() => true;
        public IEnumerable<Avatar> GetAvatars() => [new Avatar()];
        public void BeforeTask(CancellationToken ct) => Token = ct;
        public void Dispose()
        {
            Disposed++;
            if (DisposeError is { } error) throw error;
        }
    }
}
namespace BetterGenshinImpact.GameTask.AutoFight.Script
{
    public static class CombatScriptParser { public const string CurrentAvatarName = "当前角色"; }
    public sealed class CombatCommand(Action execute)
    {
        public void Execute(Model.CombatScenes scenes, CombatCommand? previous) => execute();
    }
    public sealed class CombatScript(List<string> avatarNames, List<CombatCommand> commands)
    {
        public List<string> AvatarNames { get; } = avatarNames;
        public List<CombatCommand> CombatCommands { get; } = commands;
    }
}
namespace BetterGenshinImpact.GameTask.AutoPathing.Model
{
    public sealed class WaypointForTrack
    {
        public AutoFight.Script.CombatScript? CombatScript { get; set; }
    }
}
namespace BetterGenshinImpact.GameTask
{
    public sealed class RunnerContext
    {
        public static RunnerContext Instance { get; } = new();
        public AutoFight.Model.CombatScenes Scenes { get; } = new();
        public Task<AutoFight.Model.CombatScenes?> GetCombatScenes(CancellationToken ct) =>
            Task.FromResult<AutoFight.Model.CombatScenes?>(Scenes);
    }
}
