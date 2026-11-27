<div align="center">

# Rain Meadow · Android

**Rain Meadow 的 Android 移植，附带平台标识广播与游戏内更新公告。**

[![Build](https://img.shields.io/badge/build-GitHub%20Actions-blue?style=flat-square)](.github/workflows/android-build.yml)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Game](https://img.shields.io/badge/Android%20snapshot-1.10-orange?style=flat-square)](docs/ci.md)
[![Meadow](https://img.shields.io/badge/Rain%20Meadow-0.1.16.3-purple?style=flat-square)]()

</div>

---

## 这是什么

把 Rain Meadow（雨甸）整套联机引擎搬到 Android 上，让手机玩家能和电脑玩家同房游戏。

移植版内置以下组件：

| 组件 | 作用 | 位置 |
|:--|:--|:--|
| **平台标识广播** | 安卓端在房间里上报自己是移植端，供房主端反制模组识别 | [`companion/RainMeadowAndroidExtras`](companion/RainMeadowAndroidExtras) |
| **更新公告** | 在菜单中显示新版提示和玩家公告 | [更新说明](docs/update.md) |

当前安装包里的代码位于 `src/mods/Rain-Meadow-1.16.3/Online/AndroidExtras/`。独立插件源码保留在 `companion/`。

---

## 快速开始

### 玩家

从 [Releases](https://github.com/Miaosu1097/RainMeadow-Android/releases) 下载 ZIP，将内容解压到游戏的 `mods/henpemaz_rainmeadow/`。确认 `modinfo.json` 和 `plugins/` 直接位于该目录下，然后在游戏的 Remix 菜单启用 Rain Meadow。

### 开发者

```sh
# Prepare the game assemblies under libs/game/1.10/Managed/.
sh tools/rain-meadow/ci.sh verify 1.10
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
│   └── RainMeadowAndroidExtras/  独立插件源码
├── tools/rain-meadow/            构建、打包、IL 校验脚本
├── docs/                         文档
├── test/                         更新机制测试资产
└── version.json                  玩家公告与更新数据源
```

详细导航见 **[docs/README.md](docs/README.md)**。

---

## 关键技术点

<details>
<summary><b>Android Steam 登录与联机</b></summary>

移植版使用 SteamKit2 登录，通过原生库 `libsteamnetworkingsockets.so` 处理 Steam 联机传输。

- `Online/Steam/AndroidSteamTransport.cs` — P/Invoke 封装
- `Online/Steam/AndroidSteamNativeLoader.cs` — 原生库加载

原生库与托管依赖位于 `plugins/steam-probe/`，**是运行时必需项，不能删除**。

</details>

<details>
<summary><b>平台标识与协议兼容性</b></summary>

平台标识走 Rain Meadow 的自定义包通道，保持大厅状态与玩家 ID 的序列化格式兼容。
详见 **[docs/broadcast.md](docs/broadcast.md)**。

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