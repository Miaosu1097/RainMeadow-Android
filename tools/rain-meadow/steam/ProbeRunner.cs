using System;
using System.Threading;
using RainMeadow;
using QRCoder;
using SteamKit2;
using SteamKit2.Discovery;

internal static class ProbeRunner
{
    private static int Main(string[] arguments)
    {
        ServerRecord? tcp = null;
        if (arguments.Length > 1 || (arguments.Length == 1 && !ServerRecord.TryCreateSocketServer(arguments[0], out tcp)))
        {
            Console.Error.WriteLine("Usage: SteamProbe.exe [Steam TCP IPv4:port]");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DebugLog.Enabled = true;
        DebugLog.AddListener((category, message) => Console.Error.WriteLine(category + ": " + message));
        try
        {
            SteamLobbyProbe.RunAsync(Console.WriteLine, CheckQr, true, timeout.Token, tcp == null ? null : new[] { tcp })
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            // QR challenges and authentication data are intentionally excluded from this runner.
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static void CheckQr(string? challenge)
    {
        if (challenge == null)
            return;
        using var generator = new QRCodeGenerator();
        using var code = generator.CreateQrCode(challenge, QRCodeGenerator.ECCLevel.M);
        if (code.ModuleMatrix.Count < 21)
            throw new InvalidOperationException("Steam challenge did not generate a valid QR matrix.");
        Console.WriteLine("Steam challenge encoded as a QR matrix.");
    }
}
