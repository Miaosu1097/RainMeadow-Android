using System.Security.Permissions;
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace KickAndroid
{
    /// <summary>Plugin metadata. The ID in modinfo.json also prefixes the strings.txt keys.</summary>
    public static class PluginInfo
    {
        public const string GUID = "Kick.Fcck.Android.peRw.1097";
        public const string NAME = "Kick Android";
        public const string VERSION = "1.0.0";
    }
}