# RainMeadowAndroidExtras

这是早期的独立 BepInEx 插件源码。当前 Android 安装包已经内置平台标识和玩家公告，玩家按[主 README 的安装说明](../../README.md#玩家)安装即可。

## 独立构建

该插件引用已编译的 Rain Meadow 0.1.16.3、BepInEx、Newtonsoft.Json 和游戏程序集。构建前把 `src/BroadcastKey.cs.example` 复制为 `src/BroadcastKey.cs`，并设置与接收方配套的协议常量。该本地文件由 Git 忽略。

```sh
dotnet build Extras.csproj -c Release \
  -p:GameManaged="/path/to/Managed" \
  -p:RainMeadowDll="/path/to/Rain Meadow.dll" \
  -p:NewtonsoftDll="/path/to/Newtonsoft.Json.dll"
```

构建后的模组目录位于 `bin/Release/mod/RainMeadowAndroidExtras/`，包含 `modinfo.json` 和插件 DLL。

旧版更新器读取顶层版本字段，与当前的公告清单格式不同。测试当前行为请使用包含内置扩展的 Android 主包。[当前公告说明](../../docs/update.md)
