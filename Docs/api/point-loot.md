# 逐点拾取与安全续组（pointloot.6）

本分支基于已部署的 globalpick.2 与已发布 pointloot.5，增加路线传送失败后的安全续组并保留应用全局名单。工程默认版本号沿用上游；`pointloot.6` 表示本地候选补丁的版本。配套 runtime 用 `GetPointLootCapabilities()` 在执行前核验协议、实际执行器预算和失败码；旧主程序会在路线执行前被拒绝。

发布审查另修复了共用简易策略执行器的异常传播：策略路线的 JSON 前置动作和 `combat_script` 不再吞掉未验证恢复、传送失败等异常。普通调用保留原有行为；这项修复需随新构建部署后才会在游戏中生效。

功能需要配套的 JavaScript 调用方。逐点接口不负责选线、账户冷却记录或怪物标签，调用方需要认识 `completed-with-skips` 并校验结构化结果。应用级拾取白名单与黑名单由[全局拾取名单](global-pickup.md)统一读取并执行最终否决。

## 接口与结果

`pathingScript.RunWithLootPolicyResult(routeJson, policyJson)` 执行一条完整路线，返回 JSON 字符串，字段如下：

| 字段 | 含义 |
| --- | --- |
| `schema` | 结果结构版本，当前为 `1` |
| `success` | 路线是否满足完成条件 |
| `code` | 明确的完成或路线局部失败码 |
| `routeSha256` | 输入 `routeJson` 原始字符串按 UTF-8 编码后的 SHA256，小写十六进制 |
| `message` | 执行结果和跳过点位说明 |

成功码为 `completed`、`completed-with-skips`。允许作为结构化路线局部失败返回的代码为 `position-unrecognized`、`target-too-far`、`route-stuck`、`revive-retry-exhausted`、`teleport-failed`、`teleport-timeout`。后两者表示路线自己的传送失败，经过输入释放和主界面确认，可以继续下一条路线；整条失败路线不记完成/CD，包括此前已有部分战斗完成的情况。

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

结构化接口的路线传送保留既有最多三次尝试和总计 60 秒预算；点击后缺少传送面板仍只尝试一次。只有显式分类的地图/目标操作失败或自身预算超时，才进入路线恢复。未知引擎异常、配置错误和用户停止继续抛出；识图内部异常不能伪装成普通匹配失败。

恢复先释放所有输入；只在新截图识别为大地图且没有复苏提示时发送 Esc，未知界面和加载界面不按键、不点击。最多两轮观察，每轮最多 24 帧、间隔 250 ms，必须有连续三帧主界面且没有复苏提示才能返回局部失败。未确认恢复继续抛出异常，停止配置组。

连续故障状态由每个 `AutoPathingScript` 实例持有，只由一次成功的路线传送清除。第一次安全恢复可结束本路线并继续；再次失败且期间没有成功路线传送时，即使已返回主界面也停止配置组。完成无传送路线、CD 跳过和其他局部失败都不会清除此状态；不同脚本实例和神像恢复不共用它。

JavaScript 的 runtime 和组循环都必须认识新增的两个失败码，并继续校验结构与 SHA。配套 JS 候选与本地安装文件分开准备；不认识新码的旧调用方会拒绝结果，不会误记成功。普通 `Tp` 和旧寻路接口不启用这项自动续组。

## 构建与回归检查

在仓库根目录执行：

```powershell
dotnet build BetterGenshinImpact.sln -c Debug
dotnet run --project Test/PointLootRouteResult.Regression
dotnet run --project Test/PointLootBridge.Regression
dotnet run --project Test/PointSkip.Regression
dotnet run --project Test/CombatScriptExecutor.Regression
dotnet run --project Test/RouteTeleport.Regression
node Test/RouteTeleport.Regression/route-group-tests.cjs <JS候选目录> <配套policy目录>
```

五个原生回归程序覆盖结果契约、许可生命周期、恢复边界、跳过计划、简易策略异常传播与路线传送重试。Node 检查加载实际 JS runtime 和组循环，验证失败不写 CD、下一条可执行、错误/取消停止和并发任务清理；游戏与输入边界使用替身。通过这些检查不等于真实游戏中的识图、掉落拾取或完整一条龙验收。
