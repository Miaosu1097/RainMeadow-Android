<div align="center">

# RainMeadowAndroidExtras

**Rain Meadow 安卓移植版的配套插件 —— 平台标识广播 + 更新公告。**

</div>

---

## 为什么是独立插件

不以「修改 Rain Meadow 源码」的方式实现，有两个原因：

1. **移植版本体用到了 C# 14 的 `extension` 块语法**，需要 Roslyn 5+（.NET 9 运行时）才能编译，
   而很多构建环境只有 .NET 8。
2. **独立插件只需引用已编译好的 `Rain Meadow.dll`**，用常规 Roslyn 即可构建，互不干扰。

运行效果完全一致 —— BepInEx 在 `plugins/` 目录扫描到的 DLL 会被同等地加载。

---

## 功能

| 功能 | 触发条件 | 说明 |
|:--|:--|:--|
| **平台标识广播** | 仅 Android | 每 3 秒检查房间，向其他参与者上报平台标记 |
| **更新公告** | 全平台 | 启动后拉取 `version.json`，有新版本时在主菜单弹窗 |
| **自动替换** | 全平台 | 启动最早期把 `plugins/*.new` 换成正式文件 |

---

## 安装

把 `RainMeadowAndroidExtras.dll` 放到已有 Rain Meadow 的 `plugins/` 目录：

```
<模组目录>/
└── RainMeadow/
    └── plugins/
        ├── Rain Meadow.dll
        ├── Newtonsoft.Json.dll
        └── RainMeadowAndroidExtras.dll   ← 新增这一个
```

**只新增一个文件，不覆盖任何现有内容。** 删除它即可完全卸载，无残留。

---

## 前置条件

| 项 | 要求 |
|:--|:--|
| Rain Meadow | 0.1.16.x（针对 0.1.16.3 编译） |
| BepInEx | 已启用 |
| Newtonsoft.Json | 随 Rain Meadow 一同提供 |

---

## 验证是否生效

查看 BepInEx 日志，搜 `[Extras]`：

```
[Extras] broadcast installed, key=...
[Extras] broadcast sent to 3:xxxx
[Extras] new version available: 0.1.17.0 (force=True)
```

桌面端**不会有任何 `[Extras]` 广播日志**（平台判断直接返回）。

---

## 构建

```sh
dotnet build Extras.csproj -c Release \
  -p:GameManaged=<游戏 Managed 目录> \
  -p:RainMeadowDll=<Rain Meadow.dll> \
  -p:NewtonsoftDll=<Newtonsoft.Json.dll>
```

产出 `bin/Release/mod/RainMeadowAndroidExtras/`：

```
RainMeadowAndroidExtras/
├── modinfo.json
└── plugins/RainMeadowAndroidExtras.dll
```

### 构建前必做

复制协议常量模板并填入真实值：

```sh
cp src/BroadcastKey.cs.example src/BroadcastKey.cs
# 编辑 src/BroadcastKey.cs，替换 FILL_ME_IN
```

真实值已被 `.gitignore` 排除，**必须与房主端反制模组完全一致**。

---

## 源码结构

```
src/
├── AndroidBroadcast.cs      平台标识广播（仅 Android）
├── UpdateChecker.cs         更新检查 + 下载 + 自替换
└── BroadcastKey.cs.example  协议常量模板
```

相关文档：

- [平台广播协议](../../docs/broadcast.md)
- [更新公告机制](../../docs/update.md)

---

## 已知限制

| 限制 | 说明 |
|:--|:--|
| 广播无法验证真伪 | `CustomPacket` 是自报机制，改版客户端可谎报或沉默 |
| 公告依赖 raw 域名 | `raw.githubusercontent.com` 在部分地区不可达，可换 jsDelivr |
| 下载未校验哈希 | 源被劫持时无防护，可自行扩展 `sha256` 字段 |
| 「强制更新」非技术强制 | P2P 架构无服务端，玩家仍可绕过 |

---

<div align="center">
<sub><a href="../../README.md">← 主 README</a> · <a href="../../docs/README.md">文档导航</a></sub>
</div>