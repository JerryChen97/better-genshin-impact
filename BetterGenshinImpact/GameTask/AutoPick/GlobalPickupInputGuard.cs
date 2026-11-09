using System;
using System.Threading;
using BetterGenshinImpact.Core.Config;
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
        return key == User32Helper.ToVk(context.Config.AutoPickConfig.PickKey) ||
               key == GIActions.PickUpOrInteract.ToActionKey().ToVK();
    }

    public static Result Check(User32.VK key)
    {
        var context = TaskContext.Instance();
        if (!IsPickupKey(key)) return Result.Pass;
        var config = context.Config.AutoPickConfig;
        try
        {
            var rules = GlobalPickupPolicy.Rules.GetSnapshot();
            if (!rules.IsValid) return Result.Block;
            if (!rules.HasRules) return Result.Pass;
            // 宏可能连续发送 F；等待游戏消化上一按键后再验证当前选中行。
            Thread.Sleep(60);
            using var capture = TaskControl.CaptureToRectArea(forceNew: true);
            var assets = AutoPickAssets.Get(capture, config.PickKey);
            using var prompt = capture.Find(assets.PickRo);
            if (prompt.IsEmpty())
            {
                // 大世界内没有可信交互行时不盲按，防止未识别的新掉落绕过判定；
                // 演奏等其他界面的按键保持原有语义。
                return Bv.IsInMainUi(capture) ? Result.Block : Result.Pass;
            }
            var text = ReadText(capture, prompt, config, context.SystemInfo.AssetScale);
            if (string.IsNullOrWhiteSpace(text)) return Result.Block;
            return GlobalPickupPolicy.Rules.GetSnapshot(force: true).Decide(text) == GlobalPickupDecision.Deny
                ? Result.Block : Result.Pulse;
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
