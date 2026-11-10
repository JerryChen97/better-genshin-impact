# 少量特征点回归

在 Windows 命令行运行：

```powershell
dotnet run --project Test/FeatureMatch.Regression -c Release
```

直接编译生产 `Feature2DExtensions.cs`，使用与应用相同的 OpenCvSharp 版本和真实原生库。固定合成图通过真实 SIFT 生成描述子，训练描述子控制有效对应点数。覆盖两种匹配器下的 1～3 对点、空查询、退化矩阵、正确中心坐标和未知错误继续抛出。

使用 `-p:FeatureMatchSource=<旧文件绝对路径>` 可对旧生产源做相同负对照。存储、UI 辅助函数未调用且为替身；这不是游戏内地图或传送验收。路线重试、恢复、取消、许可清理和后续路线由既有 `RouteTeleport.Regression` 与 `route-group-tests.cjs` 分别检查。
