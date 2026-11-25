using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchSteamCompression
{
    private static void Main(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: PatchSteamCompression INPUT SHARPZIPLIB OUTPUT");

        using var kit = ModuleDefinition.ReadModule(args[0]);
        using var sharpZip = ModuleDefinition.ReadModule(args[1]);
        var handleMulti = kit.GetType("SteamKit2.Internal.CMClient").Methods
            .Single(method => method.Name == "HandleMulti");
        var gzip = handleMulti.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference constructor
            && constructor.FullName == "System.Void System.IO.Compression.GZipStream::.ctor(System.IO.Stream,System.IO.Compression.CompressionMode)");

        if (gzip.Previous.OpCode != OpCodes.Ldc_I4_0
            || (gzip.Next.OpCode != OpCodes.Stloc_S && gzip.Next.OpCode != OpCodes.Stloc)
            || gzip.Next.Operand is not VariableDefinition local
            || local.VariableType.FullName != "System.IO.Compression.GZipStream")
            throw new InvalidOperationException("Unexpected SteamKit gzip constructor or local layout.");

        var managedGzip = sharpZip.GetType("ICSharpCode.SharpZipLib.GZip.GZipInputStream");
        var readConstructor = managedGzip.Methods.Single(method => method.IsConstructor
            && method.IsPublic && method.Parameters.Count == 1
            && method.Parameters[0].ParameterType.FullName == "System.IO.Stream");
        gzip.Previous.OpCode = OpCodes.Nop;
        gzip.Operand = kit.ImportReference(readConstructor);
        local.VariableType = kit.ImportReference(managedGzip);
        kit.Write(args[2]);
        Console.WriteLine("SteamKit HandleMulti now opens gzip packets with the game's managed GZipInputStream.");
    }
}
