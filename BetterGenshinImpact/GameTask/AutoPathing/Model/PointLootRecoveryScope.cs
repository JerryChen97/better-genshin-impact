using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;

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
    /// while <paramref name="ct"/> is live). It must leave any open map or panel and return true only after confirming
    /// the main UI without a revive prompt; the timeout then ends with the verified revive retry. Otherwise the
    /// timeout propagates unchanged.
    /// </param>
    internal static void RecoverThenRetry(Action recover, Func<bool> isMainUi, CancellationToken ct,
        bool defeated, RetryException retry, Func<bool>? restoreMainUiAfterTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        bool mainUi;
        try
        {
            recover(); // Unknown recovery errors must propagate; they are never safe route results.
            ct.ThrowIfCancellationRequested();
            mainUi = isMainUi();
        }
        catch (TeleportTimeoutException) when (defeated && restoreMainUiAfterTimeout is not null &&
                                               !ct.IsCancellationRequested)
        {
            if (!Checked(restoreMainUiAfterTimeout, ct)) throw;
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
    /// (handled exactly like a verified defeat) after a completed trip with a confirmed main UI, or after a statue
    /// teleport timeout (<paramref name="ct"/> live) followed by a restored and confirmed main UI. A completed trip
    /// without a confirmed main UI keeps the plain retry and its original contract. An unrestored timeout and every
    /// other recovery error propagate unchanged; cancellation always wins.
    /// </summary>
    internal static async Task<RetryException> RecoverAtStatueAsync(Func<Task> recover, Func<Task<bool>> confirmMainUi,
        Func<Task<bool>> restoreMainUiAfterTimeout, CancellationToken ct, string message)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await recover();
        }
        catch (TeleportTimeoutException) when (!ct.IsCancellationRequested)
        {
            if (!await CheckedAsync(restoreMainUiAfterTimeout, ct)) throw;
            return new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted,
                message + StatueTimeoutSuffix);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        return await CheckedAsync(confirmMainUi, ct)
            ? new PointLootRouteRetryException(PointLootRouteFailureReason.ReviveRetryExhausted, message)
            : new RetryException(message);
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
