using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>Only a policy route opts into strict, verified recovery. No persistent/global switch.</summary>
internal sealed class PointLootRecoveryScope : IDisposable
{
    internal const string StatueTimeoutSuffix = "（七天神像传送超时，已返回主界面）";

    private static readonly AsyncLocal<PointLootRecoveryScope?> Current = new();
    private readonly PointLootRecoveryScope? _previous;
    private int _disposed;

    private PointLootRecoveryScope()
    {
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static PointLootRecoveryScope Enter() => new();
    internal static bool IsActive => Current.Value is { } scope && Volatile.Read(ref scope._disposed) == 0;

    /// <param name="restoreMainUiAfterTimeout">
    /// Used only when a defeat recovery's statue teleport exceeded its own budget (<see cref="TeleportTimeoutException"/>
    /// while <paramref name="ct"/> is live) and <see cref="StatueTimeoutGuard"/> allows it. It must leave any open map or
    /// panel and return true only after confirming the main UI without a revive prompt; the timeout then ends with the
    /// verified revive retry. Otherwise the timeout propagates unchanged.
    /// </param>
    internal static void RecoverThenRetry(Action recover, Func<bool> isMainUi, CancellationToken ct,
        bool defeated, RetryException retry, Func<TeleportTimeoutException, bool>? restoreMainUiAfterTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        bool mainUi;
        try
        {
            recover(); // Unknown recovery errors must propagate; they are never safe route results.
            ct.ThrowIfCancellationRequested();
            mainUi = isMainUi();
        }
        catch (TeleportTimeoutException timeout) when (defeated && restoreMainUiAfterTimeout is not null &&
                                                       !ct.IsCancellationRequested)
        {
            if (!MayConvert(ct) || !Checked(() => restoreMainUiAfterTimeout(timeout), ct)) throw;
            StatueTimeoutGuard.Converted();
            throw new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted,
                retry.Message + StatueTimeoutSuffix);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (!mainUi) throw new InvalidOperationException("Point-policy recovery did not return to the main UI.");
        if (defeated)
            throw new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted, retry.Message);
        throw retry; // Swimming and other recovery requests retain their existing fatal-on-exhaustion contract.
    }

    /// <summary>
    /// Low-HP or revive-modal statue trip on a policy route; returns the retry to throw. It is the verified retry
    /// (handled exactly like a verified defeat) after a completed trip that <paramref name="confirmRecovered"/> accepts,
    /// or after a statue teleport timeout (<paramref name="ct"/> live, allowed by <see cref="StatueTimeoutGuard"/>)
    /// followed by a restored and confirmed main UI. A completed trip that is not confirmed keeps the plain retry and its
    /// original contract. An unrestored or repeated timeout and every other recovery error propagate unchanged;
    /// cancellation always wins.
    /// </summary>
    internal static async Task<RetryException> RecoverAtStatueAsync(Func<Task> recover, Func<Task<bool>> confirmRecovered,
        Func<TeleportTimeoutException, Task<bool>> restoreMainUiAfterTimeout, CancellationToken ct, string message,
        string timeoutMessage)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await recover();
        }
        catch (TeleportTimeoutException timeout) when (!ct.IsCancellationRequested)
        {
            if (!MayConvert(ct) || !await CheckedAsync(() => restoreMainUiAfterTimeout(timeout), ct)) throw;
            StatueTimeoutGuard.Converted();
            return new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted, timeoutMessage);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        return await CheckedAsync(confirmRecovered, ct)
            ? new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted, message)
            : new RetryException(message);
    }

    private static bool MayConvert(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StatueTimeoutGuard.MayConvert) return true;
        Logger.LogWarning("七天神像传送再次超时（上次超时后还没有完成过神像传送），视为持续故障，按原规则停止");
        return false;
    }

    /// <summary>Runs a UI check; cancellation (before, during or right after it) takes priority over its result.</summary>
    private static bool Checked(Func<bool> check, CancellationToken ct)
    {
        bool result;
        try
        {
            result = check();
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task<bool> CheckedAsync(Func<Task<bool>> check, CancellationToken ct)
    {
        bool result;
        try
        {
            result = await check();
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(Current.Value, this))
            Current.Value = _previous;
    }
}
