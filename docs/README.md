<div align="center">

# 文档导航

</div>

---

## 从哪里开始

| 你是 | 先读 |
|:--|:--|
| **玩家**，只想装上玩 | [主 README · 快速开始](../README.md#快速开始) |
| **房主**，想屏蔽移植端 | [broadcast.md · 反制对接](broadcast.md#反制模组对接) |
| **开发者**，想自己编译 | [ci.md · 本地构建](ci.md#本地构建和游戏更新) |
| **维护者**，想发版 | [update.md · 发布流程](update.md#发布流程) |

---

## 文档清单

### [ci.md](ci.md) — 构建与持续集成

- GitHub Actions 工作流说明（自托管 runner）
- 程序集私库与 `Managed.zip` 校验
- 本地构建、游戏版本升级流程
- GLES Shader Bundle 编译
- Release 产物路径

### [broadcast.md](broadcast.md) — 平台标识广播

- 为什么必须走 `CustomPacket` 而不是新增协议字段
- key / payload 约定与长度限制
- 发送端行为（仅 Android，桌面端零 hook）
- 接收端（房主反制模组）如何消费
- 自报机制的固有限制

### [update.md](update.md) — 更新公告

- `version.json` 全部字段说明
- 强制更新 vs 可选更新
- 弹窗时机（为什么必须等主菜单）
- 下载失败的三级降级
- `.new` 自替换机制与重启时机

---

## 仓库地图

```
.
├── README.md                     项目总览
├── version.json                  ★ 更新公告数据源（改这个就生效）
│
├── src/mods/
│   ├── Rain-Meadow-1.16.3/       Rain Meadow 移植源码
│   │   ├── Online/              联机核心（含 AndroidSteamTransport）
│   │   ├── Arena/               竞技场
│   │   ├── Story/               剧情模式
│   │   └── Menu/                菜单
│   └── Android-IME/              安卓输入法桥接
│       ├── AndroidImePlugin.cs
│       ├── AndroidTextEditor.java
│       └── TextInput.cs
│
├── companion/
│   └── RainMeadowAndroidExtras/  ★ 自研插件
│       ├── Extras.csproj
│       ├── modinfo.json
│       ├── README.md
│       └── src/
│           ├── AndroidBroadcast.cs   平台标识广播
│           ├── UpdateChecker.cs      更新公告 + 自替换
│           └── BroadcastKey.cs.example  协议常量模板
│
├── tools/
│   ├── rain-meadow/
│   │   ├── build.sh              主编译脚本
│   │   ├── package.py            打包 + 校验
│   │   ├── check_source.py       拒绝二进制入库
│   │   ├── game-inputs.tsv       游戏版本 → 程序集提交 → SHA-256
│   │   └── il/                   IL 审查与回放
│   └── android-ime/build.sh      输入法插件构建
│
├── test/
│   └── testpatch.txt             更新机制测试资产
│
├── docs/                         本目录
└── .github/workflows/
    ├── android-build.yml         触发入口
    └── android-run.yml           可复用构建流程
```

---

## 常见问题

<details>
<summary><b>为什么 <code>libs/</code>、<code>work/</code>、<code>release/</code> 是空的？</b></summary>

这些目录存放游戏程序集、原生库、构建中间产物，都是二进制。
`tools/rain-meadow/check_source.py` 会在 CI 第一步**拒绝任何二进制文件入库**。

需要本地构建时自行准备（见 [ci.md](ci.md)），CI 则从程序集私库拉取并校验 SHA-256。

</details>

<details>
<summary><b>为什么 <code>BroadcastKey.cs</code> 只有 <code>.example</code>？</b></summary>

协议常量抽出到未跟踪文件，是为了避免「克隆即得一份可直接编译的完整产物」。
复制模板填入真实值才能构建。详见 [broadcast.md](broadcast.md#协议常量)。

> 注意：这只提高门槛，不是可靠的安全边界 —— 运行时仍可从已编译的 DLL 中读取常量。

</details>

<details>
<summary><b>桌面端装了会怎样？</b></summary>

**什么都不会发生。**

`AndroidBroadcast.Install()` 的第一行就是平台判断：

```csharp
if (Application.platform != RuntimePlatform.Android)
    return;   // 不注册任何 hook
```

更新检查仍然工作（它不依赖平台），若不需要可自行注释。

</details>

<details>
<summary><b>能只把 DLL 发给别人吗？</b></summary>

可以。`RainMeadowAndroidExtras.dll` 是标准 BepInEx 插件，依赖项（Rain Meadow、
BepInEx、Newtonsoft.Json）对方都已经具备。

把它放到 `<模组目录>/RainMeadow/plugins/` 即可，**只新增一个文件，不覆盖任何现有内容**。

</details>

---

<div align="center">
<sub><a href="../README.md">← 返回主 README</a></sub>
</div>