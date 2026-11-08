using System;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>Only a policy route opts into strict, verified recovery. No persistent/global switch.</summary>
internal sealed class PointLootRecoveryScope : IDisposable
{
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

    internal static void RecoverThenRetry(Action recover, Func<bool> isMainUi, CancellationToken ct,
        bool defeated, RetryException retry)
    {
        ct.ThrowIfCancellationRequested();
        bool mainUi;
        try
        {
            recover(); // Unknown recovery errors must propagate; they are never safe route results.
            ct.ThrowIfCancellationRequested();
            mainUi = isMainUi();
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(Current.Value, this))
            Current.Value = _previous;
    }
}
