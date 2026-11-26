# KickAndroid（禁止移植版）

电脑端（桌面版雨世界）使用的**反制模组**：作为房主时，识别加入房间的「手机版雨甸」玩家，并可按配置踢出 / 播报。

> 本模组只对**桌面端房主**生效。非房主不注册任何 hook，完全休眠。

## 检测方式

不用 RPC、不用程序集引用，靠 Rain Meadow 内置的 `CustomPacket` 字符串路由：

| 项 | 值 |
|---|---|
| key | `KickPE_Plat` |
| payload | `android`（UTF-8） |

手机版雨甸（`AndroidBroadcast`）进入房间静默 3 秒后，**每 5 秒**向房内每个对端发一次这个极小的包。
桌面端每收到一次就检查是否已在本次房间停留期内处理过，**同一玩家同一停留期只播报一次**；玩家退出后记录清空，再进来会重新播报。

另有一条兼容通道：反射订阅 `RainMeadowPlatformMarker.PlatformMarkers.AndroidMarkerReceived` 事件，用于旧版 Marker。

## 配置（Remix 界面或 `ModConfigs/Kick.Fcck.Android.peRw.1097.txt`）

| 键 | 说明 | 默认 |
|---|---|---|
| `ActionMode` | `KICK` / `KICK_NOTIFY` / `NOTIFY_ONLY` / `SILENT` | `KICK_NOTIFY` |
| `BanInsteadOfKick` | 踢出时连带永久封禁 | `false` |
| `NotifyOnDetect` | 检测到即播报「进入房间」 | `true` |
| `WriteLog` | 写 `KickAndroid.log` | `true` |

**只想看提示、不踢人 → `NOTIFY_ONLY`。** 选 `SILENT` 时只写日志，游戏内不会有任何提示。

## 编译

`libs/` 里的引用 DLL **不随仓库分发**，需自行从游戏目录拷贝：

| 文件 | 来源 |
|---|---|
| `BepInEx.dll` | `<游戏>/BepInEx/core/` |
| `MonoMod.RuntimeDetour.dll`、`MonoMod.Utils.dll` | `<游戏>/BepInEx/core/` |
| `PUBLIC-Assembly-CSharp.dll`、`HOOKS-Assembly-CSharp.dll` | BepInEx 生成的公共化程序集 |
| `UnityEngine*.dll` | `<游戏>/RainWorld_Data/Managed/` |
| `Rain Meadow.dll` | `<游戏>/BepInEx/plugins/`（**桌面版**，不要用手机版） |

然后：

```sh
dotnet build KickAndroid.csproj -c Release
# 或没有 .NET SDK 时手动调用 Roslyn：
#   csc -target:library -langversion:latest -nostdlib+ -r:<net48>/mscorlib.dll ... src/*.cs
```

产物 `KickAndroid.dll` 放入 `<游戏>/BepInEx/plugins/`。

## 行为边界

- **不是**防作弊，只是把「移植端标记」暴露给房主做选择。
- 手机版广播是**周期性**的（每 5 秒），因此本模组的去重是必要的；去重状态只维持到该玩家离开房间。
- 播报环节自身报错时会撤销去重记录，下个周期自动补报，保证「至少成功播报一次」。
