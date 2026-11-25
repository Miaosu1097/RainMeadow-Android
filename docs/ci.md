# Rain Meadow Android 构建

在 Linux x64 的自托管 GitHub Actions runner 上添加 `rain-meadow-build` 标签。为 runner 安装 .NET 10 SDK、Mono（含 `4.8-api` 引用程序集）、Python 3.9+、curl 和 unzip。已有 .NET 8 SDK 的环境也可使用 .NET 9 或 10 运行时启动固定版本的 Roslyn 编译器。若 Mono 引用在其他位置，在 runner 上设置 `MONO_FRAMEWORK_REFS`。

在专用 runner 的仓库检出目录中准备以下输入。`libs/` 由 Git 忽略，工作流保留该检出目录里的输入。游戏快照按版本分目录，包含该 Android 版本的完整 `Managed` 引用，以及与该版本配套生成的 `HOOKS-Assembly-CSharp.dll`、BepInEx 和 MonoMod。

```text
libs/
  game/
    1.10/
      Managed/
        Assembly-CSharp.dll
        HOOKS-Assembly-CSharp.dll
        …
      shaders/
        rainmeadow
  valve/
    android-arm64/
      libsteamnetworkingsockets.so
```

设置仓库变量 `RAIN_MEADOW_GAME_REVISION=1.10` 后，推送 `main` 会编译并验证该快照。也可在 Actions 手动指定 `game_revision` 和 `build` 模式。本地与 CI 共用入口。

```sh
sh tools/rain-meadow/ci.sh verify 1.10
sh tools/rain-meadow/ci.sh package 1.10
```

- `verify` 编译启用 Steam SDR 的托管插件，执行定向 IL 回放、启动回放、HookGen 目标校验和 Mono 上的整条 IL hook 链验证。插件和报告写入日志中显示的 `work/rain-meadow/candidate.*`。
- `package` 还构建 Android ARM64 Steam 桥接库和 DEX，转换并验证音频，使用对应快照已验证的 GLES bundle。Linux runner 需要 Android NDK、JDK 17 或更高版本及 FFmpeg，并设置 `ANDROID_NDK_HOME` 和 `JAVA_HOME`。Termux 可使用现有的 clang++ 和 libc++。ZIP 输出到 `release/Rain-Meadow/<游戏版本>/`，`modinfo.json` 和 `plugins/` 位于 ZIP 根目录。[Android NDK 交叉编译文档](https://developer.android.com/ndk/guides/other_build_systems)
- Valve 库只用于 `package`。使用 `prepare_valve_android.py` 所固定的 Valve Android ARM64 原版输入。脚本校验 SHA-256，并在 `work/` 下生成副本。SteamKit2 等托管依赖由 NuGet 锁文件固定。缓存写入 `.cache/`，ZIP 保存在 runner 的 `release/`。

## 游戏更新

1. 将新版本的 `Managed/` 和配套的 HookGen 程序集放进 `libs/game/<新版本>/Managed/`。用手动 workflow 或 `ci.sh verify <新版本>` 对此版本构建。
2. CI 记录输入程序集 SHA-256，按方法签名比较 `android-targets.tsv`，并用新程序集重跑编译和 IL 验证。无关的方法和程序集元数据可以变化。遇到受保护的方法体变化、签名缺失、IL 锚点出错或 JIT 失败，工作流会失败。日志及 `game-il.tsv`、`replay.tsv`、`startup-replay.tsv`、`hook-jit.tsv` 可用于定位。
3. 如果需要修改 IL，先检查目标方法的变化，再调整 `il/stage_il.py` 及适用的回放项。确认补丁在新版上仍正确后，将审核过的清单保存为 `tools/rain-meadow/il/baselines/<新版本>.tsv`。CI 优先使用该版本的基线，其他快照继续使用各自的基线或 `android-targets.tsv`。如果同一套补丁需要支持多个快照，分别跑一次 CI。
4. 确认 Android 上的菜单、联机和 GLES bundle 后，再用 `package` 打包，随后可更新 `RAIN_MEADOW_GAME_REVISION`。

工作流通过 runner 标签选择持有游戏快照的构建机。[GitHub 自托管 runner 标签文档](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow)
