using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Audit
{
    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(AllTypes))
            yield return nested;
    }

    private static string BodyHash(MethodDefinition method)
    {
        var body = method.Body;
        var positions = body.Instructions.Select((instruction, index) => (instruction, index))
            .ToDictionary(item => item.instruction, item => item.index);
        int Position(Instruction instruction) => instruction == null ? -1 : positions[instruction];

        string Operand(object operand)
        {
            switch (operand)
            {
                case Instruction target: return Position(target).ToString(CultureInfo.InvariantCulture);
                case Instruction[] targets: return string.Join(",", targets.Select(Position));
                case VariableDefinition local: return local.Index.ToString(CultureInfo.InvariantCulture);
                case ParameterDefinition parameter: return parameter.Index.ToString(CultureInfo.InvariantCulture);
                case string literal: return Convert.ToBase64String(Encoding.Unicode.GetBytes(literal));
                case float single: return BitConverter.ToInt32(BitConverter.GetBytes(single), 0).ToString("x8", CultureInfo.InvariantCulture);
                case double number: return BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture);
                default: return Convert.ToString(operand, CultureInfo.InvariantCulture);
            }
        }

        var text = body.InitLocals + "\n" + string.Join("\n", body.Variables.Select(local => local.VariableType.FullName))
            + "\n" + string.Join("\n", body.Instructions.Select(item => item.OpCode.Name + " " + Operand(item.Operand)))
            + "\n" + string.Join("\n", body.ExceptionHandlers.Select(region => string.Join(" ",
                region.HandlerType, region.CatchType, Position(region.TryStart), Position(region.TryEnd),
                Position(region.HandlerStart), Position(region.HandlerEnd), Position(region.FilterStart))));
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    private static Dictionary<string, MethodDefinition> Methods(ModuleDefinition module) =>
        module.Types.SelectMany(AllTypes).SelectMany(type => type.Methods)
            .ToDictionary(method => method.FullName, StringComparer.Ordinal);

    private static void PrintFingerprints(string assembly, string manifest)
    {
        using (var game = ModuleDefinition.ReadModule(assembly))
        {
            var methods = Methods(game);
            foreach (var line in File.ReadLines(manifest))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    Console.WriteLine(line);
                    continue;
                }

                int separator = line.IndexOf('\t');
                if (separator < 0 || separator == line.Length - 1)
                    throw new InvalidDataException("Expected hash and method signature: " + line);
                var signature = line.Substring(separator + 1);
                var fingerprint = !methods.TryGetValue(signature, out var method) ? "missing-target"
                    : method.HasBody ? BodyHash(method) : "no-body";
                Console.WriteLine(fingerprint + "\t" + signature);
            }
        }
    }

    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 3 && args[0] == "--fingerprints")
        {
            PrintFingerprints(args[1], args[2]);
            return;
        }

        if (args.Length < 3 || args.Length > 4)
            throw new ArgumentException("Usage: Audit GAME HOOKS SUBSCRIPTIONS [PC_GAME]");

        using (var game = ModuleDefinition.ReadModule(args[0]))
        using (var hooks = ModuleDefinition.ReadModule(args[1]))
        using (var pc = args.Length == 4 ? ModuleDefinition.ReadModule(args[3]) : null)
        {
            var gameMethods = Methods(game);
            var pcMethods = pc == null ? null : Methods(pc);
            var hookTypes = hooks.Types.SelectMany(AllTypes)
                .ToDictionary(type => type.FullName.Replace('/', '.'), StringComparer.Ordinal);

            Console.WriteLine("source\thook\thandler\tstatus\tandroid_body_sha256\tpc_body\ttarget");
            foreach (var line in File.ReadLines(args[2]))
            {
                var columns = line.Split('\t');
                var hook = columns[1];
                var split = hook.LastIndexOf('.');
                var eventName = hook.Substring(split + 1);
                var status = "missing-hook";
                var hash = "";
                var comparison = "";
                var target = "";

                if (hookTypes.TryGetValue(hook.Substring(0, split), out var hookType))
                {
                    var registration = hookType.Methods.SingleOrDefault(method => method.Name == "add_" + eventName);
                    if (registration != null && registration.HasBody)
                    {
                        var tokens = registration.Body.Instructions
                            .Where(instruction => instruction.OpCode.Code == Code.Ldtoken)
                            .Select(instruction => instruction.Operand)
                            .OfType<MethodReference>().ToArray();
                        status = "unresolved-registration";
                        if (tokens.Length == 1)
                        {
                            target = tokens[0].FullName;
                            status = "missing-target";
                            if (gameMethods.TryGetValue(target, out var android))
                            {
                                status = android.HasBody ? "ok" : "no-body";
                                if (android.HasBody)
                                    hash = BodyHash(android);
                                if (pcMethods != null)
                                {
                                    if (!pcMethods.TryGetValue(target, out var desktop))
                                        comparison = "missing-target";
                                    else if (!desktop.HasBody || !android.HasBody)
                                        comparison = "no-body";
                                    else
                                        comparison = BodyHash(desktop) == hash ? "same" : "different";
                                }
                            }
                        }
                    }
                }
                Console.WriteLine(string.Join("\t", line, status, hash, comparison, target));
            }
        }
    }
}
