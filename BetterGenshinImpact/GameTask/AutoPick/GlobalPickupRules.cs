using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.AutoPick;

public enum GlobalPickupDecision { Unspecified, Allow, Deny }

/// <summary>不受任务或拾取模式切换影响的全局名单快照；黑名单始终优先。</summary>
public sealed class GlobalPickupSnapshot
{
    private readonly HashSet<string> _white;
    private readonly HashSet<string> _black;
    private readonly string[] _fuzzy;
    public bool IsValid { get; }
    public bool HasRules => _white.Count > 0 || _black.Count > 0 || _fuzzy.Length > 0;
    public bool HasWhitelist => IsValid && _white.Count > 0;

    internal GlobalPickupSnapshot(bool valid, IEnumerable<string> white, IEnumerable<string> black,
        IEnumerable<string> fuzzy)
    {
        IsValid = valid;
        _white = new HashSet<string>(white, StringComparer.Ordinal);
        _black = new HashSet<string>(black, StringComparer.Ordinal);
        _fuzzy = fuzzy.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public GlobalPickupDecision Decide(string text)
    {
        if (!IsValid || _black.Contains(text) || _fuzzy.Any(x => text.Contains(x, StringComparison.Ordinal)))
            return GlobalPickupDecision.Deny;
        return _white.Contains(text) ? GlobalPickupDecision.Allow : GlobalPickupDecision.Unspecified;
    }

    public string ToJson() => JsonConvert.SerializeObject(new
    {
        schema = 1, valid = IsValid,
        whiteList = _white.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        blackList = _black.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        fuzzyBlackList = _fuzzy
    });
}

/// <summary>
/// 原生拾取和脚本 SDK 共用的名单读取器。旧用户名单作为兼容来源合并，
/// 不把锄地结束、总开关变化或 ForceInteraction 当成清除黑名单的理由。
/// </summary>
public sealed class GlobalPickupRules
{
    private sealed class Settings
    {
        [JsonProperty("schema")] public int Schema { get; set; }
        [JsonProperty("whiteList")] public string[]? WhiteList { get; set; }
        [JsonProperty("blackList")] public string[]? BlackList { get; set; }
    }

    private readonly string _root;
    private readonly Func<long> _clock;
    private readonly Action<Exception>? _reportError;
    private readonly object _gate = new();
    private GlobalPickupSnapshot? _snapshot;
    private string? _signature;
    private string? _lastError;
    private long _lastCheck;

    public GlobalPickupRules(string installationRoot, Func<long>? clock = null, Action<Exception>? reportError = null)
    {
        _root = Path.GetFullPath(installationRoot);
        _clock = clock ?? (() => Environment.TickCount64);
        _reportError = reportError;
    }

    public GlobalPickupSnapshot GetSnapshot(bool force = false)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!force && _snapshot is not null && now - _lastCheck is >= 0 and < 100)
                return _snapshot;
            _lastCheck = now;
            try
            {
                var user = Path.Combine(_root, "User");
                var accountFolder = Path.Combine(user, "JsScript", "AutoHoeingOneDragon", "blacklists");
                var accounts = Directory.Exists(accountFolder)
                    ? Directory.GetFiles(accountFolder, "*.json").Where(x => Path.GetFileName(x) != "示例.json")
                        .OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    : [];
                var configFile = Path.Combine(user, "global-pick-rules.json");
                var whiteFile = Path.Combine(user, "pick_white_lists.txt");
                var blackFile = Path.Combine(user, "pick_black_lists.txt");
                var fuzzyFile = Path.Combine(user, "pick_fuzzy_black_lists.txt");
                var paths = new[] { configFile, whiteFile, blackFile, fuzzyFile }.Concat(accounts).ToArray();
                var signature = string.Join("|", paths.Select(x =>
                {
                    var info = new FileInfo(x);
                    return x + ":" + (info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "missing");
                }));
                if (!force && _snapshot is { IsValid: true } && signature == _signature) return _snapshot;

                var white = ReadLines(whiteFile);
                var black = ReadLines(blackFile);
                var fuzzy = ReadLines(fuzzyFile);
                if (!File.Exists(configFile))
                    throw new InvalidDataException("缺少全局拾取名单：" + configFile);
                if (File.Exists(configFile))
                {
                    var settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(configFile, Encoding.UTF8));
                    if (settings is not { Schema: 1, WhiteList: not null, BlackList: not null })
                        throw new InvalidDataException("全局拾取名单必须包含 schema=1、whiteList、blackList。");
                    white.AddRange(Names(settings.WhiteList));
                    black.AddRange(Names(settings.BlackList));
                }
                foreach (var file in accounts)
                {
                    var values = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(file, Encoding.UTF8))
                                 ?? throw new InvalidDataException("拾取黑名单不能为 null：" + file);
                    black.AddRange(Names(values));
                }
                _snapshot = new GlobalPickupSnapshot(true, white, black, fuzzy);
                _signature = signature;
                _lastError = null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                _snapshot = new GlobalPickupSnapshot(false, [], [], []);
                _signature = null;
                if (_lastError != e.Message) _reportError?.Invoke(e);
                _lastError = e.Message;
            }
            return _snapshot;
        }
    }

    private static IEnumerable<string> Names(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim() != value)
                throw new InvalidDataException("全局拾取名单包含空名称或名称两端有空白。");
            yield return value;
        }
    }

    private static List<string> ReadLines(string file) => File.Exists(file)
        ? File.ReadAllLines(file, Encoding.UTF8).Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
        : [];
}
