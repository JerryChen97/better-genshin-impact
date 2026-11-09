# 锄地一条龙全局名单适配

这些文件配合本分支的 `genshin.getGlobalPickupRules()` 使用。锄地逐点总开关仍在 `point-loot-settings.json`；应用全局名单位于安装目录的 `User/global-pick-rules.json`，不受锄地开关控制。黑名单优先于任何许可。详见[原生接口说明](../../Docs/api/global-pickup.md)。

本目录包含三个策略/运行时/拾取模块、逐点设置、名单示例，以及主脚本和 manifest 的补丁。路线、metadata、物品模板、账户记录和用户配置不在源码仓库中。

适配基线是 `2.13.4-local.point-loot.5-food.1`；`patch-base.json` 记录原始基线 SHA256 和改后文本规范化为 LF 的 SHA256。不要把补丁直接套到其他版本。先复制现有脚本到离线目录，核对基线哈希，再检查和应用补丁：

```powershell
git apply --check <本目录绝对路径>/integration.patch
git apply <本目录绝对路径>/integration.patch
```

在脚本副本目录运行以上命令。将本目录的三个 `point-loot-*.js` 和 `point-loot-settings.json` 复制到脚本副本，保留原 metadata、catalog、模板、路线、入口和记录。将名单示例作为 `User/global-pick-rules.json` 的起点，合并已有全局规则，不能覆盖用户的其他禁捡项。

只有在用户授权且运行任务结束后，才能把配套程序、脚本和名单作为一组安装。回退也应还原整组，旧主程序没有新 SDK。检查新 manifest 版本为 `2.13.4-local.point-loot.5-global.1`，启动时缺少 SDK 或无效名单会拒绝执行。不要重建 metadata。

应用全局白名单还由原生兜底触发器识别；JS 模板通道仅使用已有 catalog 中的模板，不擅自为未知物品生成模板。白名单不创建战后窗口、额外聚拢或滚动，暂停仍有效。

离线检查：在仓库根目录运行 `node Test/GlobalPickupJs.Regression/verify.cjs`。测试使用合成路线和目录，没有游戏输入。
