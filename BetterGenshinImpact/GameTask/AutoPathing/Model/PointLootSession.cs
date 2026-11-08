using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>One route invocation owns its policy, attempts and revocable pickup windows.</summary>
internal sealed class PointLootSession : IDisposable
{
    internal sealed record Policy(bool Gather, int WindowMs);

    internal sealed class Attempt
    {
        internal readonly string Token = Guid.NewGuid().ToString("N");
        internal readonly int PointIndex;
        internal readonly Policy Policy;
        internal readonly CancellationTokenSource Revoked = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long OpenedAt;
        internal bool WindowOpen;

        internal Attempt(int pointIndex, Policy policy) { PointIndex = pointIndex; Policy = policy; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Policy> _points;
    private bool _enabled = true;
    private bool _disposed;
    private Attempt? _current;

    private PointLootSession(Dictionary<int, Policy> points) { _points = points; }

    internal static PointLootSession Parse(string json, string routeJson, int pointCount, Func<int, bool> isFightPoint)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Point loot policy requires schema: 1 and a points array.", nameof(json));
        var routeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routeJson)));
        if (!root.TryGetProperty("routeSha256", out var expectedHash) || expectedHash.ValueKind != JsonValueKind.String ||
            !string.Equals(expectedHash.GetString(), routeHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Point loot policy routeSha256 does not match UTF-8 routeJson.", nameof(json));

        var policies = new Dictionary<int, Policy>();
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object ||
                !point.TryGetProperty("pointIndex", out var indexValue) || !indexValue.TryGetInt32(out var index) ||
                index < 0 || index >= pointCount || !isFightPoint(index) ||
                !point.TryGetProperty("gather", out var gather) || gather.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !point.TryGetProperty("windowMs", out var window) || !window.TryGetInt32(out var windowMs) ||
                windowMs < 0 || windowMs > 30000 || !policies.TryAdd(index, new Policy(gather.GetBoolean(), windowMs)))
                throw new ArgumentException("Invalid, duplicate or non-fight point loot policy (windowMs must be 0..30000).", nameof(json));
        }
        return new PointLootSession(policies);
    }

    internal Attempt BeginFight(int pointIndex)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RevokeLocked();
            var policy = _points.GetValueOrDefault(pointIndex) ?? new Policy(false, 0);
            var attempt = new Attempt(pointIndex, policy);
            _current = attempt;
            if (!_enabled) attempt.Revoked.Cancel();
            return attempt;
        }
    }

    internal bool CanGather(Attempt attempt)
    {
        lock (_gate)
            return !_disposed && _enabled && ReferenceEquals(_current, attempt) &&
                   !attempt.Revoked.IsCancellationRequested && attempt.Policy.Gather;
    }

    internal bool CanOpenWindow(Attempt attempt)
    {
        lock (_gate)
            return !_disposed && _enabled && ReferenceEquals(_current, attempt) &&
                   !attempt.Revoked.IsCancellationRequested && attempt.Policy.WindowMs > 0;
    }

    internal async Task RunFightAsync(int pointIndex, CancellationToken ct,
        Func<Task> fight, Func<Attempt, Task> gather)
    {
        var attempt = BeginFight(pointIndex);
        try
        {
            await fight();
            // A normal return includes an ordinary configured battle timeout. Recovery/errors throw.
            ct.ThrowIfCancellationRequested();
            await gather(attempt);
            ct.ThrowIfCancellationRequested();
            await WaitForWindowAsync(attempt, ct);
        }
        finally
        {
            EndFight(attempt);
        }
    }

    internal async Task WaitForWindowAsync(Attempt attempt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || !_enabled || !ReferenceEquals(_current, attempt) ||
                attempt.Revoked.IsCancellationRequested || attempt.Policy.WindowMs == 0) return;
            attempt.OpenedAt = Stopwatch.GetTimestamp();
            attempt.WindowOpen = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Revoked.Token);
        try
        {
            await attempt.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(attempt.Policy.WindowMs), linked.Token);
        }
        catch (TimeoutException) { /* A budget expiry ends an attempt, not a claimed pickup. */ }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt.Revoked.IsCancellationRequested) { }
        finally
        {
            lock (_gate) attempt.WindowOpen = false;
        }
        ct.ThrowIfCancellationRequested();
    }

    internal string GetContextJson()
    {
        lock (_gate)
        {
            var attempt = _current;
            if (_disposed || !_enabled || attempt is null || !attempt.WindowOpen ||
                attempt.Revoked.IsCancellationRequested ||
                Stopwatch.GetElapsedTime(attempt.OpenedAt).TotalMilliseconds >= attempt.Policy.WindowMs) return "null";
            return JsonSerializer.Serialize(new
            {
                token = attempt.Token, pointIndex = attempt.PointIndex, phase = "postBattle",
                gather = attempt.Policy.Gather, windowMs = attempt.Policy.WindowMs
            });
        }
    }

    internal bool Complete(string token)
    {
        lock (_gate)
        {
            var attempt = _current;
            if (_disposed || !_enabled || attempt is null || !attempt.WindowOpen ||
                attempt.Token != token || attempt.Revoked.IsCancellationRequested ||
                Stopwatch.GetElapsedTime(attempt.OpenedAt).TotalMilliseconds >= attempt.Policy.WindowMs) return false;
            attempt.WindowOpen = false;
            return attempt.Completion.TrySetResult();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled) RevokeLocked();
        }
    }

    internal void EndFight(Attempt attempt)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, attempt)) RevokeLocked();
            // The handler has awaited its gather/window operations before ending the attempt.
            attempt.Revoked.Dispose();
        }
    }

    internal void Revoke()
    {
        lock (_gate) RevokeLocked();
    }

    /// <summary>Revokes only this attempt. An attempt that is no longer current has already been revoked.</summary>
    internal void Revoke(Attempt attempt)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, attempt)) RevokeLocked();
        }
    }

    private void RevokeLocked()
    {
        var attempt = _current;
        _current = null;
        if (attempt is null) return;
        attempt.WindowOpen = false;
        // Registrations only complete asynchronous waits; no game actions or session re-entry occur here.
        attempt.Revoked.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            RevokeLocked();
        }
    }
}
