# 文档导航

| 用途 | 文档 |
|:--|:--|
| 安装游戏模组 | [主 README · 快速开始](../README.md#快速开始) |
| 房主屏蔽移植端 | [broadcast.md · 反制对接](broadcast.md#反制模组对接) |
| 本地编译和移植到新版本 | [ci.md · 本地构建](ci.md#本地构建和游戏更新) |
| 将构建产物发到 GitHub | [ci.md · GitHub Release](ci.md#github-release) |
| 管理游戏内公告 | [update.md · 发布流程](update.md#发布流程) |

## 仓库地图

| 目录或文件 | 用途 |
|:--|:--|
| `src/mods/Rain-Meadow-1.16.3/` | Rain Meadow 移植源码。`Online/AndroidExtras/` 包含内置的 Android 扩展。 |
| `src/mods/Android-IME/` | Android 输入法桥接。 |
| `companion/` | 可以单独构建的配套模组。 |
| `tools/rain-meadow/` | 构建、IL 验证、着色器和打包工具。 |
| `.github/workflows/` | GitHub Actions 托管构建、打包和草稿发布流程，支持配置自托管 runner。 |
| `version.json` | 游戏内的玩家公告与版本信息。 |
| `test/` | 开发用测试文件。 |
| `libs/`、`work/`、`.cache/`、`release/` | 本地构建输入、中间文件、缓存和安装 ZIP。 |

源码仓库只跟踪源码和文本资源。CI 从程序集仓库读取锁定的游戏快照，并校验 `Managed.zip` 的 SHA-256。该版本提供 GLES bundle 时，打包流程也会读取对应的着色器文件。[构建与持续集成](ci.md)
