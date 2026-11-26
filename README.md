<div align="center">

# Rain Meadow · Android

**Rain Meadow 的 Android 移植，附带平台标识广播与游戏内更新公告。**

[![Build](https://img.shields.io/badge/build-GitHub%20Actions-blue?style=flat-square)](.github/workflows/android-build.yml)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Game](https://img.shields.io/badge/Rain%20World-v1.11.8-orange?style=flat-square)]()
[![Meadow](https://img.shields.io/badge/Rain%20Meadow-0.1.16.3-purple?style=flat-square)]()

</div>

---

## 这是什么

把 Rain Meadow（雨甸）整套联机引擎搬到 Android 上，让手机玩家能和电脑玩家同房游戏。

除了移植本体，仓库还额外提供自研组件：

| 组件 | 作用 | 位置 |
|:--|:--|:--|
| **平台标识广播** | 安卓端在房间里上报自己是移植端，供房主端反制模组识别 | [`companion/RainMeadowAndroidExtras`](companion/RainMeadowAndroidExtras) |
| **更新公告** | 启动时检查仓库版本，过期则在主菜单弹窗，支持强制更新与自动替换 | 同上 |

> 两者以**独立 BepInEx 插件**形式存在，不修改 Rain Meadow 本体源码 ——
> 因此不会破坏与官方桌面版的协议互通。

---

## 快速开始

### 玩家

从 [Releases](https://github.com/Miaosu1097/RainMeadow-Android/releases) 下载最新压缩包，解压到游戏模组目录即可。

### 开发者

```sh
# 1. 准备游戏程序集（二进制，不入库）
#    放到 libs/game/<版本>/Managed/

# 2. 初始化构建环境
sh tools/rain-meadow/bootstrap_build.sh

# 3. 编译
sh tools/rain-meadow/build.sh
```

完整说明见 **[docs/ci.md](docs/ci.md)**（构建链路、IL 校验、Release 发布）。

---

## 仓库结构

```
.
├── src/mods/
│   ├── Rain-Meadow-1.16.3/       Rain Meadow 移植源码（上游快照 + 安卓改动）
│   └── Android-IME/              安卓输入法桥接
├── companion/
│   └── RainMeadowAndroidExtras/  平台广播 + 更新公告（独立插件）
├── tools/rain-meadow/            构建、打包、IL 校验脚本
├── docs/                         文档
├── test/                         更新机制测试资产
└── version.json                  更新公告数据源
```

详细导航见 **[docs/README.md](docs/README.md)**。

---

## 关键技术点

<details>
<summary><b>原生 Steam 直连（不依赖 Steamworks.NET）</b></summary>

桌面版靠 Steamworks.NET，这在 Android 上不存在。移植版通过 `dlopen` / `dlsym`
直接绑定 Valve 的 `libsteamnetworkingsockets.so`：

- `Online/Steam/AndroidSteamTransport.cs` — P/Invoke 封装
- `Online/Steam/AndroidSteamNativeLoader.cs` — 原生库加载

原生库与托管依赖位于 `plugins/steam-probe/`，**是运行时必需项，不能删除**。

</details>

<details>
<summary><b>协议兼容性：为什么不给 LobbyState 加字段</b></summary>

平台信息**不在** Rain Meadow 协议里：

- `LobbyState` 的 `[OnlineField]` 只有 mods 列表、玩家 ID、Remix 设置
- `SteamPlayerId` 只序列化 `steamID`，`LANPlayerId` 只序列化 `endPoint`
- 全库搜索 `RuntimePlatform` 仅 3 处，全在 UI 分支

如果为了传平台信息而新增字段，官方桌面版解析时**字节错位，房间直接连不上**。

所以走 `CustomManager`（`CustomPacket`）：按字符串 key 路由，与程序集版本无关，
未订阅的客户端静默丢弃。详见 **[docs/broadcast.md](docs/broadcast.md)**。

</details>

<details>
<summary><b>IL 级构建校验</b></summary>

Harmony / MonoMod 补丁依赖 IL 结构，游戏一更新就可能锚不住。
`tools/rain-meadow/il/` 提供：

- 受保护方法审查（`Audit.cs`）
- 定向 + 启动 + hook 链 IL 回放（`Replay.cs`）
- 每版本方法指纹基线（`baselines/<版本>.tsv`）

</details>

---

## 文档

| 文档 | 内容 |
|:--|:--|
| [docs/README.md](docs/README.md) | 文档导航与仓库地图 |
| [docs/ci.md](docs/ci.md) | 构建链路、CI、Release 发布 |
| [docs/broadcast.md](docs/broadcast.md) | 平台标识广播协议与反制对接 |
| [docs/update.md](docs/update.md) | 更新公告、version.json 字段、自替换机制 |

---

## 许可

本移植的**原创部分**（Android 改动、`tools/` 构建工具、`Android-IME`、`companion/`）
为 MIT，见 [LICENSE](LICENSE) 与 [LICENSES/MIT.txt](LICENSES/MIT.txt)。

`src/mods/Rain-Meadow-1.16.3/` 是上游快照
（[henpemaz/Rain-Meadow](https://github.com/henpemaz/Rain-Meadow) @ `59bbcf5`），
版权与许可归原作者所有。第三方声明（含 JZlib）随各自材料适用。

---

<div align="center">
<sub>本项目与 Videocult / Rain World 官方无关，为非官方社区移植。</sub>
</div>