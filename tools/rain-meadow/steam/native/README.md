# Rain Meadow Steam 网络探针

这是 Termux glibc 环境下的 ARM64 测试工具。它使用 Rain World 的 Steam App ID 312520 和独立的测试频道 312520。测试只收发固定格式的回环消息。启动探针无需运行 Rain World。

## 本地模拟

在项目工作区根目录运行以下命令。构建、下载的源码头文件、模拟套接字和日志都在工作区内。

```sh
sh tools/rain-meadow/steam/native/simulate.sh
```

模拟运行两个探针进程，使用本地 Unix 数据报验证对端回调、接收、回包和退出清理。模拟数据不发给 Steam。

## 原生 Steam 测试

在运行 ARM64 Steam 的同一 Termux glibc 环境中使用工作区内的文件。将下面的 STEAMCLIENT_LIBRARY 换成对应的 ARM64 Linux steamclient.so 路径。从原生 Steam 后台所用的同一用户和兼容环境启动探针，保留该环境的 IPC 配置。

```sh
chmod u+x steam-network-probe-linux-arm64
./steam-network-probe-linux-arm64 STEAMCLIENT_LIBRARY inspect
./steam-network-probe-linux-arm64 STEAMCLIENT_LIBRARY check
```

`inspect` 检查原生库加载、导出和 `SteamClient020`。`check` 检查登录、Rain World App ID 和 `SteamNetworkingMessages`。

### 单账号测试

`check` 成功后，在同一台设备执行下列命令。进程会使用 `SteamNetworkingSockets012` 建立本机 UDP 回环，检查 Rain Meadow 0.1.16.3 的状态包和自定义包字节，以及它们的传输标志。

```sh
./steam-network-probe-linux-arm64 STEAMCLIENT_LIBRARY loopback
```

这个模式只用一条已登录的 Steam 会话，不需要另一个账号或房间。它覆盖本机数据通道，跨设备会话和 Steam 中继需要另外验证。[Valve 回环接口说明](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets#CreateSocketPair)

### 双端测试

两端各有一个不同的已登录 Steam 测试账号，双方都运行探针。

```sh
./steam-network-probe-linux-arm64 STEAMCLIENT_LIBRARY listen 对端的SteamID64
./steam-network-probe-linux-arm64 STEAMCLIENT_LIBRARY ping   监听端的SteamID64
```

先启动 listen。监听窗口为 45 秒。发送端显示 Steam peer round trip verified. 即确认消息经过这两端完整往返。这个结果用于判断是否能继续把游戏数据通道接入 Rain Meadow。

SDK 头文件用于编译时对齐接口，来源为 Valve Proton 的 proton-9.0-4 标签下的 steamworks_sdk_154。测试包只包含探针与本说明，真正运行 Steam 网络测试需要现有的 Valve 客户端会话。

## Android ARM64 Valve SDR 初始化

在工作区根目录运行以下命令。探针使用工作区已有的 Android ARM64 Valve 库、Steamworks SDK 头文件和 Rain World 的 SDR 配置。它在手机上启动独立的 Termux 进程，测量 Valve 中继的可达性。

```sh
sh tools/rain-meadow/steam/native/run-valve-init.sh
```

原库的 `DT_NEEDED` 声明了 `libandroid.so`，其未定义符号中没有该系统库提供的符号。直接加载会在 Android StatsD 的 Protobuf 版本校验处中止。准备脚本先校验已验证构建的 SHA-256，再从工作副本中移除这条依赖。探针仍会先加载系统的 `libandroid.so`，再用 `RTLD_LOCAL` 加载工作副本。

初始化顺序是设置 AppID 312520、设置 Steam 公共环境、调用 `SteamDatagramClient_Init`、加载 SDR 配置并等待 `GetRelayNetworkStatus`。返回值 `100` 表示中继测量就绪。Steam 身份绑定、P2P 握手和进入房间需要后续验证。日志写到 `work/rain-meadow/1.16.3/steam-transport/valve-init/valve-init.log`。[Valve 中继状态接口](https://partner.steamgames.com/doc/api/ISteamNetworkingUtils#GetRelayNetworkStatus)

## Rain Meadow 0.1.16.3 SDR 测试包

在工作区根目录运行 `sh tools/rain-meadow/build-sdr-release.sh`。成品写入 `release/Rain-Meadow/0.1.16.3/RainMeadow-android-steam-sdr.zip`，ZIP 根目录直接包含 `modinfo.json`、`plugins/` 和资源目录。构建会用现有的 GLES 资源，对游戏程序集运行 Android IL 回放。

原生库由 `rwmeadow-native-loader.dex` 内的 Java 类通过 `System.load` 在同一个 Android 类加载器命名空间中加载。C# 通过 JNI 桥获取传输函数地址。更新时需要保留 ZIP 中 `plugins/steam-probe/` 下的 DEX 和原生库。构建 DEX 需要 JDK，D8 会缓存到工作区。[Android 原生库加载说明](https://android.googlesource.com/platform/art/+/refs/heads/main/libnativeloader/README.md)

游戏内由 SteamKit 登录并转发 P2P 信令。Valve 库生成公钥并安装 Steam 签发的证书，使用 Rain World 的 SDR 配置建立游戏传输。主机连接建立时会记录 `relayed` 或 `direct`。连接超时会记录信令收发数和 SDR 状态。

`sdr_transport_probe.cpp` 使用发布包同一份桥接库，检查原生证书请求、错误证书拒绝、Rain Meadow 游戏包的本机 UDP 回环及 SDR 就绪。游戏内连接与原版 PC 房间的同步需要实测。
