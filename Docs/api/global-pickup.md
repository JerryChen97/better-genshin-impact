# 应用全局拾取名单

全局白名单在锄地、日常任务及任务退出后保持拾取许可。全局黑名单在所有自动拾取入口否决拾取，优先于全局白名单、普通拾取模式、逐点许可及 `ForceInteraction`。

## 名单来源

在安装目录创建 `User/global-pick-rules.json`：

```json
{
  "schema": 1,
  "whiteList": ["兽肉", "禽肉"],
  "blackList": ["破损的面具"]
}
```

名称精确匹配，不包含空字符串或两端空白。读取器还合并现有 `User/pick_white_lists.txt`、`User/pick_black_lists.txt`、`User/pick_fuzzy_black_lists.txt`，以及 `User/JsScript/AutoHoeingOneDragon/blacklists/` 内除 `示例.json` 外的 JSON 数组。账户禁捡项因此在退出锄地后继续有效；模糊黑名单按包含关系匹配。内置普通拾取默认名单仍保持原有模式语义。

这是需要配套名单文件的本地分支。名单文件缺失、格式损坏或某个账户黑名单读取失败时暂停自动拾取，不把异常当作空黑名单。安装或回退时应把程序、脚本与名单一起处理。用户文件不应直接提交到源码仓库；示例和脚本适配文件位于 `Integrations/AutoHoeingOneDragon/`。

## 执行入口

普通自动拾取先执行全局判断，强制交互也需要识别当前物品。普通拾取未启用时，调度器保留只识别全局白名单的触发器，不启用额外材料许可、聚拢或滚动。

原生任务、JavaScript 及路线宏使用的输入通道在发送拾取键前再次截取画面、识别选中行并重读名单。拾取长按转换为逐次验证的单次按键，键盘和鼠标改键都经过保护，抬起及清理不受黑名单阻挡。没有拾取提示的非大世界界面不读取名单、不增加固定 60 ms 等待，保留普通长按/抬起语义，但仍有界面识别开销。同一进程的拾取识别与按键串行，原生识别拾取还核对已批准名称，防止旧画面许可按到新选中的物品。物理手动按键不由此接口拦截。

“全局”不启动已经停止的截图器，也不解除用户暂停、战斗/传送暂停或独占任务的原有保护。拾取仍需要识图成功；日志中的按键尝试不能证明物品入包。最终检查增加了每次拾取的截图与 OCR 耗时。

## 脚本接口

`genshin.getGlobalPickupRules(force)` 返回 JSON 字符串：

```json
{"schema":1,"valid":true,"whiteList":["兽肉","禽肉"],"blackList":["破损的面具"],"fuzzyBlackList":[]}
```

`force=true` 在发送按键前重读文件，不依赖文件时间戳或大小。普通读取最多缓存 100 ms。`valid=false` 时调用方必须停止拾取。脚本不能因缺少接口而退回旧的锄地专用名单。

配套 JS 使用 `pathingScript.tryPickup(expectedName, source, token, artifactPrefix)` 发送拾取，返回是否通过最终检查。`source` 为 `global`、`point` 或 `mandatory`：全局来源需要最终白名单许可；点位来源携带当前战后窗口 token，其他来源携带本次路线 SHA256。最终临界区检查选中行仍是目标、路线/窗口未结束且任务未取消或暂停。只有 catalog 明确属于圣遗物的模板族使用名称前缀匹配（如“教官”到套装部件）；材料和食材使用精确名称。最终拒绝不会回写拾取记录。

## 验证

```powershell
dotnet build BetterGenshinImpact.sln -c Debug
dotnet run --project Test/GlobalPickup.Regression
dotnet run --project Test/GlobalPickupGuard.Regression
node Test/GlobalPickupJs.Regression/verify.cjs
```

回归覆盖生产名单读取器、输入通道和配套 JS 的许可、热更新、取消及令牌边界；识图与真实输入边界使用替身。通过检查不等于真实游戏的 OCR、拾取或完整一条龙验收。
