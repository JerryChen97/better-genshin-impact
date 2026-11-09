BetterGI 可复用 CLI 部署与 SDK E2E

从源码仓库运行。禁止屏幕/CUA/UI Automation测试及强杀；进程生命周期仅正常关闭匹配PID/启动时间/Session的实例。通过独立临时计划任务在S2运行BGI本身的 --startGroups，不派发ABGI。

先按源码与检查记录生成独立 package-manifest.json 和安装/回退包，明确 sourcePath/sourceCommit/sourceTree/sourceIndex、version、nativeContracts/jsContracts/contractChecks、files 与 protectedFiles。sourceIndex 是包目录中仅用于只读校验的 Git index，必须对应已审查的精确树。允许先对固定未提交树部署/测试，再按用户授权提交；所有检查在前后核验实际 sourceIndex/sourceTree，不能用同一HEAD下的旧脏工作树结果冒充。

只读预检：
  powershell -NoProfile -File Tools/BetterGI.E2E/Invoke-BetterGiE2E.ps1 -Mode Plan -PackageDir <工作区artifacts内的包目录> -OutDir <新的报告目录>
回归：将 -Mode 改为 Contracts。
部署：在管理员Session1使用 -Mode Deploy -Apply，或通过 Windows 标准 RunAs、隐藏窗口执行同目录Run-Elevated.ps1并传入同一PackageDir/OutDir。不操作权限弹窗。

框架校验精确剩余68条历史完成、雪山屏蔽、候选/回退哈希、保护文件和精确源码；正常关闭两个BGI与既有游戏，再原子安装、重启并跑两组真实宿主SDK探针。SDK探针只读全局规则并检查跨组保持；不临时改写真实规则，用户并发编辑原样保留并使本次检查停止。全局热更新/损坏保护由生产代码回归覆盖。探针不执行战斗、传送、拾取或实际路线输入，错配策略在创建执行器前拒绝。SDK成功、失败或超时结束都会正常关闭测试游戏；发出启动前即标记尝试，启动状态回写失败或原生组未结束时，先按唯一runId正常关闭本次SDK的BGI，阻止延迟冷启动产生新游戏；只有确认游戏退出、SDK尝试结束且原生组结束，才归档测试组与JS，无法确认则保留失败证据并报错。SDK检查不是世界掉落入包或真实慢传送失败的实机验收。

协议自测：python -I -X utf8 Tools/BetterGI.E2E/test_protocol.py；另有 test_source_guard.py 校验实际Git树与检查来源，Test-GameCleanup.ps1 与 Test-AtomicInstall.ps1 检查失败收尾和NTFS原子替换。
报告：plan/contracts/verify-install/live-result/game-close/archive/complete.json；失败看 *-error.json。每次进入部署使用新OutDir。User/config.json仅由应用自然保存，代理不替换；metadata、账号数据与既有组按哈希保护。安装文件失败按一致回退包恢复，回退有冲突不启动混合版本。

本地单EXE发布必须使用完整原生依赖自提取配置：
  dotnet publish BetterGenshinImpact/BetterGenshinImpact.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=PointLootLocal -p:Version=<版本号> -o <离线发布目录>
不要只复制framework-dependent输出的EXE而遗漏旁边的WPF原生DLL。Session-Control会观察启动5秒，提前退出明确报错；真实宿主SDK仍是后续成功条件。Test-Startup.ps1覆盖提前退出与存活分支。
