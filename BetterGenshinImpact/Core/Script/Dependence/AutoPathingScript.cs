using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Dependence;

public partial class AutoPathingScript
{
    private object? _config = null;
    private string _rootPath;
    private readonly LimitedFile _autoPathingFile;
    private readonly object _runGate = new();
    private bool _running;
    private bool _pointLootEnabled = true;
    private PointLootSession? _pointLootSession;
    private string? _pointLootRouteSha256;

    public AutoPathingScript(string rootPath, object? config)
    {
        _config = config;
        _rootPath = rootPath;
        _autoPathingFile = new LimitedFile(Global.Absolute(@"User\AutoPathing"));
    }

    public async Task Run(string json)
    {
        var entered = false;
        try
        {
            EnterRun();
            entered = true;
            var task = PathingTask.BuildFromJson(json);
            var pathExecutor = new PathExecutor(CancellationContext.Instance.Cts.Token);
            if (_config != null && _config is PathingPartyConfig patyConfig)
            {
                pathExecutor.PartyConfig = patyConfig;
            }

            await pathExecutor.Pathing(task);
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogDebug(e,"执行地图追踪时候发生错误");
            TaskControl.Logger.LogError("执行地图追踪时候发生错误: {Msg}",e.Message);
        }
        finally
        {
            if (entered) ExitRun();
        }
    }

    /// <summary>
    /// Runs one complete route with an invocation-local point policy. Unlike Run, failures propagate.
    /// Point indices refer to the original positions array; acknowledgement only ends a pickup attempt.
    /// </summary>
    public async Task RunWithLootPolicy(string routeJson, string policyJson)
    {
        // Keep the deployed API's reject-on-failure contract for older JS callers.
        await RunWithLootPolicyCore(routeJson, policyJson, false);
    }

    /// <summary>
    /// Returns an identity-bound result for complete routes or explicitly typed route-local failures.
    /// Cancellation, policy/configuration errors and all other failures still propagate.
    /// </summary>
    public Task<string> RunWithLootPolicyResult(string routeJson, string policyJson) =>
        RunWithLootPolicyCore(routeJson, policyJson, true);

    private async Task<string> RunWithLootPolicyCore(string routeJson, string policyJson, bool returnLocalFailure)
    {
        EnterRun();
        PointLootSession? session = null;
        PathExecutor? executor = null;
        try
        {
            var task = PathingTask.BuildFromJson(routeJson);
            if (task.Positions.Count == 0) throw new ArgumentException("Route has no positions.", nameof(routeJson));
            session = PointLootSession.Parse(policyJson, routeJson, task.Positions.Count,
                index => task.Positions[index].Action == ActionEnum.Fight.Code);
            lock (_runGate)
            {
                session.SetEnabled(_pointLootEnabled);
                _pointLootSession = session;
                _pointLootRouteSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routeJson))).ToLowerInvariant();
            }
            var ct = CancellationContext.Instance.Cts.Token;
            using var registration = ct.Register(session.Revoke);
            ct.ThrowIfCancellationRequested();
            // 逐点跳过只对返回结构化结果的新接口开放；旧接口保持失败即拒绝的契约。
            executor = new PathExecutor(ct) { PointLootSession = session, PointSkipEnabled = returnLocalFailure };
            if (_config is PathingPartyConfig partyConfig) executor.PartyConfig = partyConfig;
            using var recoveryScope = PointLootRecoveryScope.Enter();
            try
            {
                await RunnerContext.Instance.StopAutoPickRunTask(() => executor.Pathing(task));
            }
            catch (PointLootRouteFailureException failure) when (returnLocalFailure)
            {
                ct.ThrowIfCancellationRequested();
                return RouteResult(routeJson, false, failure.Code, failure.Message);
            }
            ct.ThrowIfCancellationRequested();
            if (!executor.SuccessEnd) throw new InvalidOperationException("Point-policy route did not complete.");
            var skipped = executor.SkippedFightPoints;
            if (skipped.Count == 0) return RouteResult(routeJson, true, "completed", "完整路线执行结束");
            var skippedText = string.Join("、", skipped.Select(s => "#" + s.OriginalIndex + "(" + s.Code + ")"));
            if (executor.CompletedFightPoints.Count > 0)
            {
                // 至少完成一个战斗点：路线按完成处理（写 CD），被跳过的点位当天不再补打。
                var message = "路线执行结束，逐点跳过 " + skipped.Count + " 个战斗点：" + skippedText;
                TaskControl.Logger.LogWarning("{Message}", message);
                return RouteResult(routeJson, true, "completed-with-skips", message);
            }

            // 一个战斗点都没完成：仍按本地失败返回（不写 CD），失败码取最后一次跳过的原因。
            var noneMessage = "所有战斗点都被跳过：" + skippedText;
            TaskControl.Logger.LogWarning("{Message}", noneMessage);
            return RouteResult(routeJson, false, skipped[^1].Code, noneMessage);
        }
        finally
        {
            try
            {
                session?.Dispose();
                executor?.ClearPointLootRun();
            }
            finally
            {
                ExitRun();
            }
        }
    }

    private static string RouteResult(string routeJson, bool success, string code, string message) =>
        JsonSerializer.Serialize(new
        {
            schema = 1,
            success,
            code,
            routeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routeJson))).ToLowerInvariant(),
            message
        });

    public string GetPointLootContext()
    {
        lock (_runGate) return _pointLootSession?.GetContextJson() ?? "null";
    }

    public bool CompletePointLootWindow(string token)
    {
        lock (_runGate) return _pointLootSession?.Complete(token) ?? false;
    }

    public void SetPointLootEnabled(bool enabled)
    {
        lock (_runGate)
        {
            _pointLootEnabled = enabled;
            _pointLootSession?.SetEnabled(enabled);
        }
    }

    private void EnterRun()
    {
        lock (_runGate)
        {
            if (_running) throw new InvalidOperationException("This pathingScript instance already has an active route.");
            _running = true;
        }
    }

    private void ExitRun()
    {
        lock (_runGate)
        {
            _pointLootSession = null;
            _pointLootRouteSha256 = null;
            _running = false;
        }
    }

    public async Task RunFile(string path)
    {
        try
        {
            var json = await new LimitedFile(_rootPath).ReadText(path);
            await Run(json);
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogDebug(e,"读取文件时发生错误");
            TaskControl.Logger.LogError("读取文件时发生错误: {Msg}",e.Message);
        }
    }

    /// <summary>
    /// 从已订阅的内容中获取文件
    /// </summary>
    /// <param name="path">在 `\User\AutoPathing` 目录下获取文件</param>
    public async Task RunFileFromUser(string path)
    {
        var json = await AutoPathingFile.ReadText(path);
        await Run(json);
    }

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否存在
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>存在返回 true，否则返回 false</returns>
    public bool IsExists(string subPath) => AutoPathingFile.IsExists(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件返回 true，否则返回 false</returns>
    public bool IsFile(string subPath) => AutoPathingFile.IsFile(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件夹
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件夹返回 true，否则返回 false</returns>
    public bool IsFolder(string subPath) => AutoPathingFile.IsFolder(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件夹的内容（非递归方式）
    /// 目录不存在时返回空数组，不会自动创建目录
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的子目录路径，默认为相对根目录</param>
    /// <returns>文件夹内所有文件和文件夹的相对路径数组，出错时返回空数组</returns>
    public string[] ReadPathSync(string subPath = "./") => AutoPathingFile.ReadPathSync(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件的文本内容
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的文件路径</param>
    /// <returns>文件文本内容，读取失败时返回空字符串</returns>
    public string ReadTextSync(string subPath) => AutoPathingFile.ReadTextSync(subPath);

    /// <summary>
    /// LimitedFile 实例，用于操作 AutoPathing 目录
    /// </summary>
    private LimitedFile AutoPathingFile => _autoPathingFile;
}
