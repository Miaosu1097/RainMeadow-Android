<div align="center">

# 更新公告与自动替换

**让已安装的模组自己发现新版本，并把新文件换进去。**

</div>

---

## 一、整体流程

```
启动
 │
 ├─ ① ExtrasPlugin.OnEnable
 │     └─ 检查 plugins/*.new → 有则替换成正式文件（此时旧 DLL 尚未被加载）
 │
 ├─ ② 首帧 RainWorld.Update
 │     └─ 异步拉取 version.json（不阻塞，10 秒超时）
 │
 ├─ ③ 主菜单就绪（currentMainLoop is Menu.Menu）
 │     └─ 若有新版本 → 弹窗
 │           force=true  → 单个「确定」按钮
 │           force=false → 「确定」下载 / 关闭窗口 = 本次忽略（24h）
 │
 ├─ ④ 点「确定」
 │     └─ 下载到 plugins/<target>.new
 │           ├─ 成功 → 弹「已下载，请重启」
 │           ├─ 失败 → 浏览器打开直链
 │           └─ 再无 → 打开仓库 releases 页面
 │
 └─ ⑤ 下次启动回到 ①
```

---

## 二、`version.json`

放在**仓库根目录**，通过 `raw.githubusercontent.com` 实时读取 ——
不占 GitHub API 的 60 次/小时限额。

```json
{
  "latest": "0.1.17.0",
  "force": true,
  "notes": "【更新内容】\n- 修复 xxx\n- 新增 yyy",
  "download": "https://example.com/path/to/file.dll",
  "target": "Rain Meadow.dll"
}
```

### 字段说明

| 字段 | 类型 | 必填 | 说明 |
|:--|:--|:--|:--|
| `latest` | string | ✅ | 最新版本号，需大于 `RainMeadow.MeadowVersionStr` 才触发 |
| `force` | bool | — | `true` = 强制更新（不可跳过）；缺省 `false` |
| `notes` | string | — | 公告正文，支持 `\n` 换行 |
| `download` | string | — | 直链。为空时跳过下载，直接开浏览器 |
| `target` | string | — | 要替换的文件名（相对 `plugins/`）。**缺省 `Rain Meadow.dll`** |

### 版本比较

复用 Rain Meadow 自带逻辑：

```csharp
RainMeadow.RainMeadow.IsNewerVersion(ver, RainMeadow.RainMeadow.MeadowVersionStr)
```

分段数字比较，`0.1.17.0 > 0.1.16.3` ✅

---

## 三、为什么弹窗必须等主菜单

`OnModsInit` 阶段 `ProcessManager` 还没就绪，此时**无法创建 Dialog**
（`Menu` 尚未初始化，`ShowDialog` 会抛异常或静默失败）。

所以：

```csharp
if (hasResult && !dialogShown && IsMenuReady(self))
{
    dialogShown = true;
    ShowDialog(self);
}

private static bool IsMenuReady(RainWorld self)
{
    var pm = self.processManager;
    return pm != null && pm.currentMainLoop is Menu.Menu;
}
```

**副作用**：如果玩家一直停在「加入房间」界面而不回主菜单，公告会延后显示。
这是可接受的——毕竟主菜单是必经路径。

---

## 四、强制 vs 可选更新

### 可选更新（`force: false`）

`DialogNotify` 只有一个按钮，所以用**两步确认**模拟「更新 / 忽略」：

```
第 1 步：[X 关闭] = 本次忽略（写 24h 记录）   [确定] → 第 2 步
第 2 步：[X 关闭] = 取消                      [确定] → 开始下载
```

忽略记录存在 `BepInEx/config/RainMeadowAndroid.update.txt`：

```
0.1.17.0|<到期 UTC ticks>
```

同一版本 24 小时内不再提示。

### 强制更新（`force: true`）

只有一步：

```
[确定] → 立即下载
```

**注意**：这种「强制」是**约定层面**的，不是技术强制。
玩家仍可关掉网络、改 hosts、或删掉插件来绕过。

真正的技术强制只能在服务端实现 —— 而雨甸是 P2P，没有服务端。

---

## 五、自替换机制

### 问题

游戏运行中，`Rain Meadow.dll` 已被 mono **mmap 映射**，Windows / Android 都会锁定文件，无法直接覆盖。

### 方案

分两步，跨越一次重启：

```
第 1 次运行                        重启后
────────────                      ────────────
下载 → plugins/X.dll.new    →     启动最早期：X.dll.new → X.dll
提示「请重启」                      （旧文件先备份为 X.dll.bak）
```

**替换点在 `ExtrasPlugin.OnEnable()`**，此时：

- Rain Meadow 尚未加载 → 目标文件未被锁定
- 但仍能执行文件操作

### 失败处理

替换失败（权限、占用）时：

- 保留 `.new` 文件，下次启动继续尝试
- 记一条 `[Extras] update apply failed`
- **不影响游戏正常启动**

---

## 六、发布流程

### 1. 改 `version.json`

```json
{
  "latest": "0.1.17.0",
  "force": false,
  "notes": "本次修复了 xxx",
  "download": "https://github.com/<owner>/<repo>/releases/download/v0.1.17.0/RainMeadow.dll",
  "target": "Rain Meadow.dll"
}
```

### 2. 提交

```sh
git add version.json
git commit -m "Release 0.1.17.0"
git push
```

**提交后立刻对所有人生效**（走 raw 域名，无 CDN 缓存延迟，通常 1 分钟内）。

### 3. 打 Release

CI 会构建产物并创建 draft Release（见 [ci.md](ci.md)）。
`download` 里的 URL 要指向**该 Release 的资产直链**。

> ⚠️ 顺序建议：**先发 Release，再改 version.json**。
> 否则玩家会看到公告但下载 404。

### 4. 回滚

把 `latest` 改回当前版本号即可，玩家端不会触发。

---

## 七、测试方法

仓库内置了一套测试资产：

```
test/testpatch.txt                 ← 「新版」文件内容
version.json                       ← 指向该文件，force=true
```

配合一个「旧版」文件，就能在不碰真实 DLL 的情况下验证全链路：

1. 把 `RainMeadowAndroidExtras.dll` + 一个旧的 `testpatch.txt` 放进 `plugins/`
2. 启动游戏 → 应看到强制更新公告
3. 点确定 → 下载 → 提示重启
4. 重启 → `testpatch.txt` 内容应被替换

验证点是「文件内容变化」，即使替换失败也不会破坏游戏。

---

## 八、安全提示

### raw 域名可能不可达

`raw.githubusercontent.com` 在部分地区被墙。若玩家反馈「看不到公告」，
首先确认这个域名是否可达。

可选的替代方案：

- 换用 `jsDelivr`：`https://cdn.jsdelivr.net/gh/<owner>/<repo>@main/version.json`
- 或自建 CDN

### 下载内容未校验

当前实现**不校验下载文件的哈希**。如果下载源被劫持，会替换进恶意 DLL。

若要加固，可在 `version.json` 加 `sha256` 字段，下载后比对再落盘。

### 自替换权限

Android 上，若模组安装在只读分区（如 split APK 内部），
替换会失败并记录日志。BepInEx 插件目录通常可写，一般无此问题。

---

<div align="center">
<sub><a href="README.md">← 文档导航</a> · <a href="../README.md">主 README</a></sub>
</div>