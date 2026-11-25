using System;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Dump
{
    private static System.Collections.Generic.IEnumerable<TypeDefinition> Types(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Types))
            yield return nested;
    }

    private static string Operand(Instruction instruction)
    {
        var operand = instruction.Operand;
        if (operand is Instruction target)
            return "IL_" + target.Offset.ToString("x4");
        if (operand is Instruction[] targets)
            return string.Join(", ", targets.Select(target => "IL_" + target.Offset.ToString("x4")));
        if (operand is string text)
            return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
        return Convert.ToString(operand, CultureInfo.InvariantCulture);
    }

    private static void Main(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Usage: Dump ASSEMBLY TYPE [METHOD]");

        using (var module = ModuleDefinition.ReadModule(args[0]))
        {
            var requested = args[1].Replace('+', '/');
            var type = module.Types.SelectMany(Types).SingleOrDefault(item => item.FullName == requested);
            if (type == null)
                throw new ArgumentException("Type not found: " + requested);

            foreach (var method in type.Methods.Where(method => args.Length == 2 || method.Name == args[2]))
            {
                Console.WriteLine(method.FullName);
                if (!method.HasBody || args.Length == 2)
                    continue;
                foreach (var local in method.Body.Variables)
                    Console.WriteLine("  .local " + local.Index + " " + local.VariableType.FullName);
                foreach (var instruction in method.Body.Instructions)
                    Console.WriteLine($"  IL_{instruction.Offset:x4} {instruction.OpCode} {Operand(instruction)}".TrimEnd());
            }
        }
    }
}
