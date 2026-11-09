using System;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.ONNX.SVTR;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>输入通道的最终保护：脚本宏、强制交互也不能越过全局禁捡项。</summary>
public static class GlobalPickupInputGuard
{
    public enum Result { Pass, Block, Pulse }
    private static readonly object InputGate = new();
    [ThreadStatic] private static PickupRequest? _request;

    private sealed class PickupRequest(string name, bool globalOnly, bool prefix, Func<bool>? stillPermitted)
    {
        internal readonly string Name = name;
        internal readonly bool GlobalOnly = globalOnly;
        internal readonly bool Prefix = prefix;
        internal readonly Func<bool>? StillPermitted = stillPermitted;
        internal bool Accepted;
    }

    public static bool TryPickup(User32.VK key, string name, bool globalOnly = false,
        bool artifactPrefix = false, Func<bool>? stillPermitted = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var previous = _request;
        var request = new PickupRequest(name, globalOnly, artifactPrefix, stillPermitted);
        _request = request;
        try
        {
            InputHub.Foreground.Keyboard.KeyPress(key);
            return request.Accepted;
        }
        finally { _request = previous; }
    }

    // 截图、名单检查和实际按键必须在同一临界区；否则原生拾取与 JS
    // 可以同时批准同一行，第二次 F 会落到已经切换的下一件物品。
    public static IDisposable? Enter(User32.VK key)
    {
        if (!IsPickupKey(key)) return null;
        Monitor.Enter(InputGate);
        return new InputLease();
    }

    private sealed class InputLease : IDisposable
    {
        public void Dispose() => Monitor.Exit(InputGate);
    }

    private static bool IsPickupKey(User32.VK key)
    {
        var context = TaskContext.Instance();
        if (!context.IsInitialized) return false;
        var configured = context.Config.AutoPickConfig.PickKey;
        User32.VK pickKey;
        try { pickKey = User32Helper.ToVk(string.IsNullOrWhiteSpace(configured) ? "F" : configured.Trim()); }
        catch (ArgumentException) { pickKey = User32.VK.VK_F; }
        var action = GIActions.PickUpOrInteract.ToActionKey();
        return key == pickKey || (action is not (KeyId.None or KeyId.Unknown) && key == action.ToVK());
    }

    public static Result Check(User32.VK key)
    {
        var context = TaskContext.Instance();
        if (!IsPickupKey(key)) return Result.Pass;
        var config = context.Config.AutoPickConfig;
        try
        {
            // 先区分非拾取界面；演奏等普通按键不受名单格式或固定等待影响。
            using (var initial = TaskControl.CaptureToRectArea(forceNew: true))
            {
                using var initialPrompt = initial.Find(AutoPickAssets.Get(initial, config.PickKey).PickRo);
                if (initialPrompt.IsEmpty() && !Bv.IsInMainUi(initial))
                    return _request is null ? Result.Pass : Result.Block;
            }
            // 宏可能连续发送 F；等待游戏消化上一按键后再验证当前选中行。
            Thread.Sleep(60);
            using var capture = TaskControl.CaptureToRectArea(forceNew: true);
            var assets = AutoPickAssets.Get(capture, config.PickKey);
            using var prompt = capture.Find(assets.PickRo);
            if (prompt.IsEmpty())
            {
                // 大世界内没有可信交互行时不盲按，防止未识别的新掉落绕过判定；
                // 演奏等其他界面的按键保持原有语义。
                return _request is null && !Bv.IsInMainUi(capture) ? Result.Pass : Result.Block;
            }
            var text = ReadText(capture, prompt, config, context.SystemInfo.AssetScale);
            if (string.IsNullOrWhiteSpace(text)) return Result.Block;
            var rules = GlobalPickupPolicy.Rules.GetSnapshot(force: true);
            if (rules.Decide(text) == GlobalPickupDecision.Deny) return Result.Block;
            var request = _request;
            if (request is not null)
            {
                var matches = text == request.Name || (request.Prefix && text.StartsWith(request.Name, StringComparison.Ordinal));
                if (!matches || rules.Decide(request.Name) == GlobalPickupDecision.Deny) return Result.Block;
                if (request.GlobalOnly && rules.Decide(text) != GlobalPickupDecision.Allow &&
                    !(request.Prefix && rules.Decide(request.Name) == GlobalPickupDecision.Allow)) return Result.Block;
                if (request.StillPermitted is not null && !request.StillPermitted()) return Result.Block;
                request.Accepted = true;
            }
            return Result.Pulse;
        }
        catch (Exception error)
        {
            TaskControl.Logger.LogDebug(error, "全局禁捡保护无法确认当前交互，不发送拾取键");
            return Result.Block;
        }
    }

    internal static string ReadText(ImageRegion capture, Region prompt, AutoPickConfig config, double scale)
    {
        var rect = new Rect(prompt.X + (int)(config.ItemTextLeftOffset * scale), prompt.Y,
            (int)((config.ItemTextRightOffset - config.ItemTextLeftOffset) * scale), prompt.Height);
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
            rect.Right > capture.SrcMat.Width || rect.Bottom > capture.SrcMat.Height) return "";
        string text;
        if (config.OcrEngine == nameof(PickOcrEngineEnum.Yap))
        {
            using var mat = new Mat(capture.CacheGreyMat, rect);
            text = TextInferenceFactory.Pick.Value.Inference(mat);
        }
        else
        {
            using var mat = new Mat(capture.SrcMat, rect);
            text = OcrFactory.Paddle.Ocr(mat);
        }
        return AutoPickTrigger.ProcessOcrText(text);
    }
}
