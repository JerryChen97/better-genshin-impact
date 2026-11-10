# 本机当前版本

`local/current` 是本机使用版本的唯一日常维护分支。当前主程序为 `0.66.3-local.pointloot.6-globalpick.2-mapmatch.1`，配套锄地脚本为 `2.13.4-local.point-loot.6-global.2`。安装产物、脚本摘要和生产源码树记录在 [LOCAL-CURRENT.json](LOCAL-CURRENT.json)。

此分支保留逐点拾取、逐点跳过、全局黑白名单、60秒路线传送预算、120秒神像预算和已确认恢复后的安全续组。最近一次修复让少于4组特征点或空单应矩阵返回无匹配，避免原生匹配异常中止整组；未知配置/引擎异常与用户取消仍按既有规则处理。

此前的逐点拾取 v5、全局名单和 v6 分支本来就是相继包含的历史，当前分支继承全部提交。旧分支头通过清单中的 `archive/20261109/*` 标签追溯，后续修复继续提交到 `local/current`。尚未部署的寻路复苏候选不属于当前安装；其原工作树和本地归档保留。

## 核对本机安装

在本分支根目录运行：

```powershell
powershell -NoProfile -File Tools/BetterGI.E2E/Verify-LocalCurrent.ps1 -InstallRoot "C:\Program Files\BetterGI"
```

该命令只读取分支、生产源码树、JS集成子树、安装EXE和六个脚本文件，检查两个源码目录是否有额外改动，并按LF规范化脚本文本计算摘要。它不启动程序或任务，不读取账号记录/凭据，也不写入安装目录。只有源码、版本与安装文件一致才报告通过。每次更新实际安装后，应更新本清单及对应验证事实。

## 构建和配套脚本

```powershell
dotnet publish BetterGenshinImpact/BetterGenshinImpact.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=PointLootLocal -p:Version=0.66.3-local.pointloot.6-globalpick.2-mapmatch.1 -o out/local-current
```

必须使用完整原生依赖自提取发布配置。清单中的EXE摘要标识已安装产物；不同路径或环境重编译不保证二进制逐字节一致。JS源码和受基线摘要约束的补丁见 [Integrations/AutoHoeingOneDragon](Integrations/AutoHoeingOneDragon/README.md)，正常部署和只读SDK验证入口见 [CLI说明](Tools/BetterGI.E2E/README.txt)。

实际User配置、路线、metadata、名单、账号记录、日志、凭据和EXE不随本分支发布。使用已有部署包时须重新核对当前安装与进程，并保留运行后产生的进度；不能用旧User备份覆盖新记录。

## 已有验证

发布对应的代码通过802项离线检查、完整Debug构建和完整Release发布，已安装宿主SDK通过28项检查。真实OpenCV少点/退化矩阵回归：旧源16项中8项失败，新源16项全部通过；同时覆盖正确定位与未知错误继续抛出。JS回归覆盖第8/258条安全失败后继续执行后250条且不改失败记录。

本机精确251条续跑已确认实际传送、移动、战斗和后续路线衔接。发布时整组仍在运行，游戏内没有主动诱发原少点帧；这些证据不等于整组完成或原异常的游戏内复现验收。
