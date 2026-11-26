namespace RainMeadow
{
    /// <summary>
    /// 广播协议常量。
    /// 必须与房主端反制模组（companion/KickAndroid 的 BroadcastKey）完全一致。
    /// </summary>
    internal static class BroadcastKey
    {
        internal const string PacketKey = "KickPE_Plat";
        internal const string Payload = "android";
    }
}