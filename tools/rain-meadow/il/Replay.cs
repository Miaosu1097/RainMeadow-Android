using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

internal static partial class Replay
{
    private static IEnumerable<TypeDefinition> NestedTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var child in type.NestedTypes.SelectMany(NestedTypes))
            yield return child;
    }

    private static void RedirectLogging(ModuleDefinition module)
    {
        var type = module.GetType("RainMeadow.RainMeadow");
        var write = module.ImportReference(typeof(Console).GetMethod("WriteLine", new[] { typeof(object) }));
        foreach (var method in type.Methods.Where(method =>
            (method.Name == "Debug" || method.Name == "Info" || method.Name == "Warn" || method.Name == "Error")
            && method.Parameters.Count > 0 && method.Parameters[0].ParameterType.FullName == "System.Object"))
        {
            method.Body = new Mono.Cecil.Cil.MethodBody(method);
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, write));
            il.Append(il.Create(OpCodes.Ret));
        }
    }

    private static Assembly LoadProbe(string plugin, string managed, string dependencies, bool startup = false, bool batch = false)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (var directory in new[] { managed, dependencies })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        using (var module = ModuleDefinition.ReadModule(plugin))
        using (var image = new MemoryStream())
        {
            if (startup || batch)
                RedirectDeathContextLogging(module);
            if (!startup)
                RedirectLogging(module);
            if (batch)
                RedirectUnityLogging(module);
            module.Write(image);
            return Assembly.Load(image.ToArray());
        }
    }

    private static void AttachProbeLogger(object instance, Type owner)
    {
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (type.FullName != "BepInEx.BaseUnityPlugin")
                continue;
            var field = type.GetField("<Logger>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(type.FullName, "<Logger>k__BackingField");
            var log = new ManualLogSource("Rain Meadow offline replay");
            log.LogEvent += (_, entry) => Console.WriteLine(entry.Level + ": " + entry.Data);
            field.SetValue(instance, log);
            return;
        }
    }

    private static void GenerateCases(string plugin, string audit)
    {
        using (var module = ModuleDefinition.ReadModule(plugin))
        {
            var handlers = module.Types.SelectMany(NestedTypes).SelectMany(type => type.Methods)
                .Where(method => method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == typeof(ILContext).FullName)
                .ToLookup(method => method.Name);
            foreach (var line in File.ReadLines(audit).Skip(1))
            {
                var columns = line.Split('\t');
                if (columns[3] != "ok")
                    continue;

                var matches = handlers[columns[2]].ToArray();
                if (matches.Length != 1)
                {
                    Console.Error.WriteLine("Unresolved manipulator: " + columns[0] + " " + columns[2]);
                    continue;
                }
                Console.WriteLine(string.Join("\t", matches[0].DeclaringType.FullName.Replace('/', '+'), matches[0].Name, columns[6]));
            }
        }
    }

    private static void RedirectDeathContextLogging(ModuleDefinition module)
    {
        var contextualizer = module.GetType("RainMeadow.Game.DeathContextualizer");
        var bindViolence = contextualizer.Methods.Single(method => method.Name == "BindViolence");
        foreach (var instruction in bindViolence.Body.Instructions)
        {
            if (!(instruction.Operand is MethodReference call) || call.DeclaringType.FullName != "RWCustom.Custom"
                || call.Parameters.Count != 1 || call.HasThis || call.ReturnType.FullName != "System.Void")
                continue;

            // Game logging requires Unity. Let the offline test surface hook warnings as failures.
            if (call.Name == "Log")
            {
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
            }
            else if (call.Name == "LogWarning")
                instruction.Operand = module.ImportReference(typeof(Replay).GetMethod(nameof(FailOnHookWarning)));
        }
    }

    public static void FailOnHookWarning(object message) =>
        throw new InvalidOperationException("BindViolence logged a warning: " + message);

    private static bool IsViolenceCall(Instruction instruction) =>
        (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
        && instruction.Operand is MethodReference call
        && call.DeclaringType.FullName == "Creature" && call.Name == "Violence";

    private static int CheckDeathContextStartup(string managed, string plugin, string dependencies)
    {
        var assembly = LoadProbe(plugin, managed, dependencies, startup: true);
        var game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        var binder = assembly.GetType("RainMeadow.Game.DeathContextualizer", throwOnError: true)
            .GetMethod("BindViolence", BindingFlags.Static | BindingFlags.NonPublic);
        var targets = new[]
        {
            (Type: "ElectricDeath", Method: "Update"),
            (Type: "Explosion", Method: "Update"),
            (Type: "KingTusks+Tusk", Method: "ShootUpdate"),
            (Type: "Pomegranate", Method: "Collide"),
            (Type: "UnderwaterShock", Method: "Update"),
            (Type: "Watcher.FlameJet", Method: "Update"),
            (Type: "Watcher.LightningMaker+StrikeAOE", Method: "Update"),
        };

        var failed = false;
        Console.WriteLine("target\tstatus\toriginal_calls\tremaining_calls");
        foreach (var target in targets)
        {
            var name = target.Type + "::" + target.Method;
            try
            {
                var original = game.GetType(target.Type, throwOnError: true).GetMethod(target.Method);
                using (var dmd = new DynamicMethodDefinition(original))
                using (var context = new ILContext(dmd.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                {
                    var before = context.Body.Instructions.Count(IsViolenceCall);
                    context.Invoke(il => binder.Invoke(null, new object[] { il, original }));
                    var after = context.Body.Instructions.Count(IsViolenceCall);
                    if (DetourHelper.GetNativeStart(dmd.Generate()) == IntPtr.Zero)
                        throw new InvalidProgramException("Mono returned a null native entry point");
                    // On this Android build FlameJet.Update has no direct Creature.Violence call.
                    var expectsMatch = target.Type != "Watcher.FlameJet";
                    if ((before > 0) != expectsMatch || after != 0)
                        throw new InvalidProgramException($"Unexpected original calls: {before} before and {after} after");
                    Console.WriteLine($"{name}\tcompiled\t{before}\t{after}");
                }
            }
            catch (Exception error)
            {
                failed = true;
                Console.WriteLine($"{name}\tfailed");
                Console.Error.WriteLine($"{name}\n{error}");
            }
        }
        return failed ? 1 : 0;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--generate")
        {
            GenerateCases(args[1], args[2]);
            return 0;
        }

        if (args.Length == 5 && args[0] == "--jit-all")
            return CheckHookChains(args[1], args[2], args[3], args[4]);

        if (args.Length == 4 && args[0] == "--startup")
            return CheckDeathContextStartup(args[1], args[2], args[3]);

        if (args.Length != 4)
            throw new ArgumentException("Usage: Replay MANAGED_DIR PLUGIN DEPENDENCIES CASES.tsv or Replay --startup MANAGED_DIR PLUGIN DEPENDENCIES");

        var standardOut = Console.Out;
        var path = Path.Combine(args[0], "Assembly-CSharp.dll");
        var assembly = LoadProbe(args[1], args[0], args[2]);
        foreach (var line in File.ReadLines(args[3]).Where(line => line.Length != 0 && line[0] != '#'))
        {
            var columns = line.Split('\t');
            var log = new StringWriter();
            Console.SetOut(log);
            string result;
            string detail;
            try
            {
                using (var resolver = new DefaultAssemblyResolver())
                using (var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    resolver.AddSearchDirectory(Path.GetFullPath(args[0]));
                    resolver.AddSearchDirectory(Path.GetFullPath(args[2]));
                    var method = module.Types.SelectMany(NestedTypes).SelectMany(type => type.Methods)
                        .Single(candidate => candidate.FullName == columns[2]);
                    var owner = assembly.GetType(columns[0], throwOnError: true);
                    var callback = owner.GetMethod(columns[1], BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance, null, new[] { typeof(ILContext) }, null);
                    if (callback == null)
                        throw new MissingMethodException(columns[0], columns[1]);
                    var instance = callback.IsStatic ? null : FormatterServices.GetUninitializedObject(owner);
                    if (instance != null)
                        AttachProbeLogger(instance, owner);
                    var handler = (ILContext.Manipulator)Delegate.CreateDelegate(typeof(ILContext.Manipulator), instance, callback);
                    var before = method.Body.Instructions.Count;
                    using (var context = new ILContext(method) { ReferenceBag = RuntimeILReferenceBag.Instance })
                        context.Invoke(handler);
                    var after = method.Body.Instructions.Count;
                    detail = $"IL instruction count {before} -> {after}";
                    result = before == after ? "same-count" : "changed";
                }
            }
            catch (Exception error)
            {
                result = "exception";
                detail = error.ToString();
            }
            finally
            {
                Console.SetOut(standardOut);
            }
            if (log.GetStringBuilder().Length != 0)
            {
                detail += " | " + log.ToString().Trim();
                if (result != "exception")
                    result += "-logged";
            }
            Console.WriteLine(string.Join("\t", columns[1], result, detail.Replace("\r", "").Replace("\n", " | ").Replace("\t", " "), columns[2]));
        }
        return 0;
    }
}
