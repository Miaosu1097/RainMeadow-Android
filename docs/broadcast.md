<div align="center">

# 平台标识广播

**让安卓端在联机房间里上报「我是移植端」。**

</div>

---

## 一、为什么必须走旁路通道

Rain Meadow 的协议里**没有任何平台字段**：

| 位置 | 序列化内容 |
|:--|:--|
| `Lobby.LobbyState` | mods 列表、玩家 ID、Remix 设置、bannedUsers… |
| `SteamPlayerId.CustomSerialize` | 只写 `steamID`（ulong） |
| `LANPlayerId` | 只写 `endPoint`（IP:Port） |

全库搜索 `RuntimePlatform` / `Application.platform`，只有 3 处，**全部在 UI 分支**（菜单音乐、房间列表），与联机协议无关。

### 为什么不能直接加字段

```csharp
// Lobby.LobbyState 的现有字段
[OnlineField] public string timeline;
[OnlineField] public ushort nextId;
[OnlineField] public string[] requiredmods;
[OnlineField] public string[] bannedmods;
[OnlineField] public string[] whitelistmods;   // 移植版已经加过一个
[OnlineField(nullable = true)] public DynamicOrderedPlayerIDs bannedUsers;
// ...
```

如果再塞一个 `platform` 字段：

- 官方桌面版解析 `LobbyState` 时**字节错位** → 房间直接连不上
- 已发布的移植版之间也会因为字段数不同而互相不兼容

**所以只能走 `CustomManager`（`CustomPacket`）：**

- 按字符串 key 路由，**与程序集版本无关**（不同于 SoftRPC 的 `ModuleVersionId`）
- `OnlineManager.SendCustomData` 只校验**接收方**是否订阅了该 key，发送方无需订阅
- 未订阅的客户端会静默丢弃，不报错、不崩服

---

## 二、协议常量

| 项 | 说明 |
|:--|:--|
| channel | Rain Meadow `CustomManager` |
| key | 字符串，长度 ≤ `CustomManager.maxLength`（**12**） |
| payload | UTF-8 字符串，接收端逐字符比较 |
| transport | `NetIO.SendType.Reliable` |

### 常量不入库

真实值**不在仓库中**，只提供模板：

```
companion/RainMeadowAndroidExtras/src/BroadcastKey.cs.example
```

构建前复制为 `BroadcastKey.cs` 并填入真实值（已被 `.gitignore` 排除）。

> **这不是安全边界。** 运行时仍可从已编译的 DLL 直接读出常量字符串。
> 它的作用仅是让「克隆仓库」不能立刻产出一份可用构建。

---

## 三、发送端

`companion/RainMeadowAndroidExtras/src/AndroidBroadcast.cs`

### 启用条件

```csharp
internal static void Install()
{
    if (installed) return;
    installed = true;

    if (Application.platform != RuntimePlatform.Android)
        return;              // ← 桌面端直接返回，不注册任何 hook

    On.RainWorld.Update += RainWorld_Update;
}
```

### 发送节奏

```
每 3 秒检查一次
├── 无房间 / 房间未激活 → 清空记录，跳过
├── 换了房间           → 清空记录，立即重发
└── 否则遍历 participants
    ├── 跳过自己 / 已离开 / 已成功发送过的
    ├── 已达 8 次尝试上限 → 跳过
    ├── 前置检查对端是否已同步该 key（否则会被静默丢弃）
    └── CustomManager.SendCustomData(...)
```

### 为什么要前置检查

`OnlineManager.SendCustomData` 里有一段：

```csharp
if (!lobby.clientSettings.TryGetValue(toPlayer, out var settings) || ...) return;
if (!customSettings.keys.Contains(customPacket.key)) return;   // ← 对端没订阅就丢弃
```

如果对端（房主）还没把自己的 `CustomClientSettings.keys` 同步过来，这次发送会**静默消失且无任何报错**。

所以发送前先本地查一次，不满足就留到下一轮重试，避免白白消耗重试次数。

---

## 四、反制模组对接

房主端需要一个订阅同名 key 的模组，才能收到标记。

### 接收端最小实现

```csharp
public sealed class Handler : IUseCustomPackets
{
    public bool Active => true;

    public void ProcessPacket(OnlinePlayer fromPlayer, CustomPacket packet)
    {
        if (packet?.data == null) return;

        string payload = Encoding.UTF8.GetString(packet.data, 0, packet.dataSize);
        if (payload != BroadcastKey.Payload) return;

        // fromPlayer 是网络层写入的发送者，不可伪造
        OnAndroidDetected(fromPlayer);
    }
}

CustomManager.Subscribe(BroadcastKey.PacketKey, new Handler());
```

### 防伪造要点

**不要**把发送者身份作为参数传进包里。使用 `packet.from` / `ProcessPacket` 的 `fromPlayer` ——
那是网络层根据连接写入的，客户端无法伪造。

### 去重

同一玩家可能因重连、换房等原因多次上报。建议在**同一房间内**按玩家去重，
换房或离开后重置：

```csharp
if (!ReferenceEquals(lastLobby, lobby)) { reported.Clear(); lastLobby = lobby; }
reported.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));
if (!reported.Add(player)) return;   // 已处理过
```

---

## 五、行为对照

| 场景 | 结果 |
|:--|:--|
| 手机端进「房主没装反制模组」的房间 | 包照常发出，无人订阅 → 被丢弃 → **无任何事发生** |
| 手机端进「房主装了反制模组」的房间 | 被识别 → 按房主配置踢出 / 封禁 / 仅提示 |
| 桌面端运行 | 不注册 hook，**零输出、零影响** |
| 手机端在单人模式 | 无 lobby，不发送 |

---

## 六、已知限制

### 自报机制无法验证

`CustomPacket` 是**自报**的。装载改版客户端的人可以：

- 谎报自己是安卓（导致自己被踢）
- 或者干脆不报（规避检测）

这是协议层的固有限制：Rain Meadow 拿不到真实平台信息，服务端无法验证。
任何**在客户端执行**的检测都能被客户端绕过。

### key 长度上限

`CustomManager.maxLength = 12`。超长的 key 会被截断（`Substring(0, 12)`），
导致两端 key 不一致而收不到包。

### 对端需先同步

新加入房间的玩家，其 `CustomClientSettings` 需要一轮状态同步才会到达其他客户端。
这段时间内发送会失败——已由重试机制覆盖（最多 8 次，每 3 秒一轮）。

---

<div align="center">
<sub><a href="README.md">← 文档导航</a> · <a href="../README.md">主 README</a></sub>
</div>