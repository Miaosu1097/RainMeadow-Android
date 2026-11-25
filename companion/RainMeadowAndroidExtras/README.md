# RainMeadowAndroidExtras

为 Rain Meadow 安卓移植版补充两项能力。**不改动 Rain Meadow 本体**，以独立 BepInEx 插件形式加载。

## 功能

| 功能 | 说明 |
|---|---|
| 平台标识广播 | 仅 Android 生效。向房间内其他参与者上报平台标记，供房主端「禁止移植版」识别。桌面端不注册任何 hook。 |
| 更新公告 | 启动后异步拉取仓库根目录的 `version.json`，有新版本时在主菜单弹窗。支持强制/非强制两种模式。 |

## 广播协议

| 项 | 值 |
|---|---|
| 通道 | Rain Meadow `CustomManager`（`CustomPacket`，字符串 key 路由） |
| key | 见 `src/BroadcastKey.cs.example`，长度需 ≤ 12 |
| payload | 见模板 |
| 传输 | `NetIO.SendType.Reliable` |

真实常量不在仓库中（已 gitignore），需自行复制模板填入。
必须与房主端「禁止移植版」的 `BroadcastKey` 完全一致。

## 更新公告数据源

仓库根目录的 `version.json`：

```json
{
  "latest": "0.1.17.0",
  "force": false,
  "notes": "本次更新内容",
  "download": "https://github.com/<owner>/<repo>/releases/download/<tag>/<file>"
}
```

- `latest` 大于当前 `RainMeadow.MeadowVersionStr` 时触发
- `force: true` 时只有一个确认按钮；`false` 时可取消，且 24 小时内不再提示同一版本
- `download` 为空或下载失败时改用浏览器打开链接

## 构建

```sh
dotnet build companion/RainMeadowAndroidExtras/Extras.csproj -c Release \
  -p:GameManaged=<Managed 目录> \
  -p:RainMeadowDll=<Rain Meadow.dll> \
  -p:NewtonsoftDll=<Newtonsoft.Json.dll>
```

产出 `bin/Release/mod/RainMeadowAndroidExtras/`，内含 `modinfo.json` 与 `plugins/`。

## 安装

把 `RainMeadowAndroidExtras.dll` 放到已有 Rain Meadow 的 `plugins/` 目录即可。
只新增一个文件，不覆盖任何现有内容。
