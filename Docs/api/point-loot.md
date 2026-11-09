# 逐点拾取与逐点跳过（pointloot.5）

本分支把本地 pointloot.5 原生补丁整理到更新后的官方 `main`，提供按战斗点授权拾取、逐点跳过和七天神像传送恢复。工程默认版本号沿用上游；`pointloot.5` 表示这组本地补丁的版本。

功能需要配套的 JavaScript 调用方。原生接口不负责选线、账户冷却记录、怪物标签或拾取黑名单，调用方需要认识 `completed-with-skips` 并校验结构化结果。

## 接口与结果

`pathingScript.RunWithLootPolicyResult(routeJson, policyJson)` 执行一条完整路线，返回 JSON 字符串，字段如下：

| 字段 | 含义 |
| --- | --- |
| `schema` | 结果结构版本，当前为 `1` |
| `success` | 路线是否满足完成条件 |
| `code` | 明确的完成或路线局部失败码 |
| `routeSha256` | 输入 `routeJson` 原始字符串按 UTF-8 编码后的 SHA256，小写十六进制 |
| `message` | 执行结果和跳过点位说明 |

成功码为 `completed`、`completed-with-skips`。允许作为结构化路线局部失败返回的代码为 `position-unrecognized`、`target-too-far`、`route-stuck`、`revive-retry-exhausted`。

调用方应先校验结构、`success`、结果码和路线 SHA，再更新完成状态或冷却记录。无返回值、未识别结果和抛出的异常都不能当作完成。

`RunWithLootPolicy` 保留失败时抛出的契约；旧的 `Run` 接口保留原有的捕获并记录错误行为。逐点跳过只在 `RunWithLootPolicyResult` 路径启用。

## 点位与拾取窗口

- 点位索引对应原始路线的 `positions` 数组。策略会校验路线身份、索引范围和战斗动作。
- 拾取许可按本次调用和当前窗口隔离，通过 `GetPointLootContext()` 获取。过期、撤销或其他窗口的令牌不能完成当前窗口。
- `CompletePointLootWindow(token)` 只确认一次拾取尝试结束，不证明实际获得了掉落。
- `SetPointLootEnabled(false)` 会撤销当前许可。路线结束、失败或取消时会清理许可与恢复作用域。

## 逐点跳过

可确认的定位失败、移动卡死或复苏恢复耗尽，可以只跳过当前遭遇，再从本段自己的传送点重放路径。同位置连续战斗点作为一个遭遇处理；重放会抑制已处理遭遇及之前的战斗等动作，`combat_script` 仍按原路线逻辑执行。

至少完成一个战斗点时，带跳过的路线返回 `completed-with-skips`，结果中列出跳过点位。所有战斗点都被跳过时返回路线局部失败。调用方据此决定是否记录完成，不能只看“地图追踪结束”的日志。

## 传送与取消边界

七天神像传送预算为 120 秒，路线传送预算仍为 60 秒。自己的传送预算到期会抛出 `TeleportTimeoutException`，与用户取消分开处理。

复苏或低血量恢复中的神像传送超时，只有在返回主界面、连续确认主界面且没有复苏提示后，才允许进入安全重试；未能确认恢复时仍抛出异常。若此前已经把一次神像超时按恢复重试处理，此后尚无一次神像传送完成，再次超时会直接抛出异常。

用户停止、策略或配置错误、未验证的恢复，以及其他异常继续抛给调用方。路线传送超时或传送失败仍可能结束调用方的整组任务；本补丁不提供任意传送错误后的自动续组保证。

## 构建与回归检查

在仓库根目录执行：

```powershell
dotnet build BetterGenshinImpact.sln -c Debug
dotnet run --project Test/PointLootRouteResult.Regression
dotnet run --project Test/PointLootBridge.Regression
dotnet run --project Test/PointSkip.Regression
```

三个回归程序覆盖结果契约、许可生命周期、恢复边界和跳过计划，其中部分执行器与游戏交互使用替身。通过这些检查不等于验证了真实游戏中的识图、掉落拾取或一次完整的一条龙运行。
