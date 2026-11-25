# Rain Meadow Android 构建

Git 主仓库保存源码、文本资源、脚本和输入版本记录。构建时取得的 DLL、音频、图片、AssetBundle 和原生库进入 Git 忽略的 `libs/`、`work/` 或 `.cache/`，安装包写到 `release/`。Action 在拉取外部输入前检查 Git 跟踪的文件。

## GitHub Actions

默认使用 GitHub 托管的 `ubuntu-24.04`。镜像提供 .NET、Python、JDK 17 和 GitHub CLI。Action 安装 Mono，打包时再安装 FFmpeg 并选择 Android NDK `29.0.14206865`。如果镜像未预装该版本，脚本会将 NDK 安装到检出目录的 `.cache/`。[GitHub Ubuntu 24.04 runner](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md)

程序集仓库默认指向 `MISTAKE568/Rain-Meadow-Android-Assemblies`。可用变量 `RAIN_MEADOW_ASSEMBLIES_REPOSITORY` 改地址。公开仓库可用 Actions 自带 token 读取。使用私库时，在源码仓库设置仅有程序集仓库 `Contents: Read` 权限的 `RAIN_MEADOW_ASSEMBLIES_TOKEN`。Action 按 `tools/rain-meadow/game-inputs.tsv` 里的提交检出版本目录，验证 `Managed.zip` 的 SHA-256 后解包到 `work/`。[GitHub 跨仓库检出说明](https://github.com/actions/checkout#checkout-multiple-repos-private)

推送 `main` 默认验证 `1.10`。可用变量 `RAIN_MEADOW_GAME_REVISION` 切换默认版本，也可手动指定 `game_revision` 和 `build`。

- `verify` 编译启用 Steam SDR 的托管插件，执行定向 IL 回放、启动回放、HookGen 目标校验和 Mono 上的整条 IL hook 链验证。插件和报告写入 `work/rain-meadow/candidate.*`。
- `package` 还从固定的上游 Meadow 提交取得 `Mod/` 和 `Shaders/`，校验文件内容并叠加仓库中的 Android 文本改动。Valve 原版输入优先使用 `libs/valve/android-arm64/libsteamnetworkingsockets.so`，缺少时从固定的 Steam CDN 归档取得。构建会校验归档和库，编译 ARM64 桥接库与 DEX，并通过 FFmpeg 验证音频。自托管 Linux runner 需提供 Android NDK、JDK 17+ 和 FFmpeg，设置 `ANDROID_NDK_HOME` 与 `JAVA_HOME`。Termux 可使用现有的 clang++ 和 libc++。[NDK 交叉编译文档](https://developer.android.com/ndk/guides/other_build_systems)
- 要在 `package` 时编译 GLES bundle，提供该游戏版本的 `libs/game/<游戏版本>/shaders/current-gles-template.asset`，并为 runner 准备 UnityPy 1.25.3、`vkd3d-compiler`、`spirv-cross`、`spirv-val` 和 `glslangValidator`。已有验证过的 bundle 也可放在同目录的 `rainmeadow`。如果程序集仓库的固定版本目录已提供 `shaders/`，Action 优先使用该目录。GitHub 托管 runner 会在发现该目录的模板时自动安装 Shader 编译工具和 UnityPy。可通过 `RAIN_MEADOW_GLES_TEMPLATE` 或 `RAIN_MEADOW_GLES_BUNDLE` 指定输入，通过 `RAIN_MEADOW_VKD3D` 和 `RAIN_MEADOW_SPIRV_CROSS` 指定编译工具。

ZIP 输出到 `release/Rain-Meadow/<游戏版本>/`，`modinfo.json` 和 `plugins/` 位于 ZIP 根目录。SteamKit2 等托管依赖由 NuGet 锁文件固定。当前程序集仓库的 `1.10` 快照只有 `Managed.zip` 和校验文件。要在托管 runner 上打包，还需将对应 GLES bundle 或模板提供给工作流，例如放进程序集仓库 `1.10/shaders/`，再更新主仓库锁定的提交。

## GitHub Release

为准备发布的源码提交创建并推送标签，例如 `v0.1.16.3-android.1`。在 Actions 手动运行 **Rain Meadow Android**，选择与该标签同一提交的 ref，并填写 `release_tag`。当提供标签时，Action 自动走完整打包模式，成功后在源码仓库创建 Release 草稿，并附上 ZIP 和 `.sha256`。可在 Releases 页面编辑说明并发布草稿。只有这条手动路径申请 `contents: write`，使用 Actions 自带 token 上传。[GitHub CLI 的草稿发布](https://cli.github.com/manual/gh_release_create)

## 自托管 runner

如需使用自己机器上的 GLES 文件，可把仓库变量 `RAIN_MEADOW_RUNNER` 设置为 `["self-hosted","linux","x64","rain-meadow-build"]`，并注册带对应标签的 Linux x64 runner。安装 .NET 10 SDK（或 .NET 8 SDK 配 .NET 9/10 运行时）、Mono（含 `4.8-api` 引用程序集）、Python 3.9+、curl、unzip。若 Mono 引用目录不同，设置 `MONO_FRAMEWORK_REFS`。打包还需上述 NDK、JDK 和 FFmpeg，创建 Release 草稿需安装 `gh`。

## 本地构建和游戏更新

本地可以使用已解包的 `libs/game/<游戏版本>/Managed/`，或者传入私库中已固定版本的 ZIP。

```sh
RAIN_MEADOW_GAME_ARCHIVE=work/rain-meadow/assembly-source/1.10/Managed.zip \
  sh tools/rain-meadow/ci.sh verify 1.10
sh tools/rain-meadow/ci.sh package 1.10
```

本地已有固定上游提交的源码 ZIP 时，可以通过 `RAIN_MEADOW_UPSTREAM_ARCHIVE` 提供，构建仍会校验 `Mod/` 和 `Shaders/` 的内容。

1. 在程序集仓库的新版本目录放入 `Managed.zip`，其中包含该版本的项目引用和配套的 `HOOKS-Assembly-CSharp.dll`。把版本名、程序集仓库提交及 ZIP 的 SHA-256 加入 `tools/rain-meadow/game-inputs.tsv`。
2. 用 `verify` 对新版本编译。CI 会记录程序集 SHA-256，比较受保护的方法，并重跑 IL 验证。目标方法体变化、签名缺失、IL 锚点出错或 JIT 失败时，查看 `game-il.tsv`、`replay.tsv`、`startup-replay.tsv` 和 `hook-jit.tsv`。
3. 调整需适配的补丁与回放项。审核后将该版本的指纹保存为 `tools/rain-meadow/il/baselines/<新版本>.tsv`。CI 优先使用该版本的基线。若一套补丁支持多个版本，分别跑各版本的 CI。
4. 用对应版本的 GLES 输入构建 `package`，并在 Android 上验证菜单与联机。
