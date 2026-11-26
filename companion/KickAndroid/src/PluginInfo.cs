using System.Security.Permissions;
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace KickAndroid
{
    /// <summary>模组元信息。id 必须与 modinfo.json 的 id 完全一致（strings.txt 的键由它拼出）。</summary>
    public static class PluginInfo
    {
        public const string GUID = "Kick.Fcck.Android.peRw.1097";
        public const string NAME = "Kick Android";
        public const string VERSION = "1.0.0";
    }
}