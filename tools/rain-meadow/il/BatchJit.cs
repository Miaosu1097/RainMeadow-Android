using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

internal static partial class Replay
{
    private sealed class Registration
    {
        internal string Location;
        internal string Target;
        internal string CallbackType;
        internal string CallbackName;
        internal bool Startup;
    }

    private static bool IsRegistration(MethodReference call) =>
        call.DeclaringType.Scope.Name == "HOOKS-Assembly-CSharp"
        && call.DeclaringType.FullName.StartsWith("IL.", StringComparison.Ordinal)
        && call.Name.StartsWith("add_", StringComparison.Ordinal);

    private static Dictionary<string, MethodDefinition> MethodIndex(ModuleDefinition module) =>
        module.Types.SelectMany(NestedTypes).SelectMany(type => type.Methods)
            .ToDictionary(method => method.FullName, StringComparer.Ordinal);

    private static MethodReference FindCallback(Instruction registration)
    {
        for (var step = registration.Previous; step != null; step = step.Previous)
        {
            if (step.OpCode.Code == Code.Ldftn || step.OpCode.Code == Code.Ldvirtftn)
                return step.Operand as MethodReference;
            if ((step.Operand is MethodReference earlier && IsRegistration(earlier))
                || step.OpCode.Code == Code.Ret)
                break;
            if (registration.Offset - step.Offset > 50)
                break;
        }
        return null;
    }

    private static bool IsReflectiveRegistration(Instruction code, MethodReference call) =>
        code.OpCode.Code == Code.Newobj && call.DeclaringType.FullName == "MonoMod.RuntimeDetour.ILHook";

    private static string FindPropertyGetter(Instruction code, ModuleDefinition game)
    {
        var window = new List<Instruction>();
        for (var step = code.Previous; step != null && code.Offset - step.Offset <= 45; step = step.Previous)
        {
            window.Add(step);
            if (step.OpCode.Code == Code.Ldtoken && step.Operand is TypeReference owner)
            {
                var property = window.FirstOrDefault(item => item.OpCode.Code == Code.Ldstr)?.Operand as string;
                if (!window.Any(item => item.Operand is MethodReference accessor
                    && accessor.DeclaringType.FullName == "System.Reflection.PropertyInfo" && accessor.Name == "GetGetMethod"))
                    return null;
                return game.GetType(owner.FullName)?.Properties.SingleOrDefault(item => item.Name == property)?.GetMethod?.FullName;
            }
        }
        return null;
    }

    private static List<Registration> RegistrationOrder(string plugin, string managed)
    {
        using (var module = ModuleDefinition.ReadModule(plugin))
        using (var hookgen = ModuleDefinition.ReadModule(Path.Combine(managed, "HOOKS-Assembly-CSharp.dll")))
        using (var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll")))
        {
            var pluginMethods = MethodIndex(module);
            var declarations = MethodIndex(hookgen);
            var registrations = new List<Registration>();
            var visited = new HashSet<MethodDefinition>();

            void Visit(MethodDefinition caller, bool startup)
            {
                if (!caller.HasBody || !visited.Add(caller))
                    return;
                foreach (var code in caller.Body.Instructions)
                {
                    if (!(code.Operand is MethodReference callee))
                        continue;
                    bool callsMethod = code.OpCode.Code == Code.Call || code.OpCode.Code == Code.Callvirt;
                    bool generated = callsMethod && IsRegistration(callee);
                    bool reflective = IsReflectiveRegistration(code, callee);
                    if (generated || reflective)
                    {
                        string target = null;
                        if (reflective)
                            target = FindPropertyGetter(code, game);
                        else if (declarations.TryGetValue(callee.FullName, out var declaration))
                            target = declaration.Body.Instructions
                                .Where(item => item.OpCode.Code == Code.Ldtoken)
                                .Select(item => item.Operand).OfType<MethodReference>().SingleOrDefault()?.FullName;

                        var callback = FindCallback(code);
                        bool known = callback != null && pluginMethods.TryGetValue(callback.FullName, out var handler)
                            && handler.Parameters.Count == 1 && handler.Parameters[0].ParameterType.FullName == "MonoMod.Cil.ILContext";
                        registrations.Add(new Registration
                        {
                            Location = $"{caller.FullName} IL_{code.Offset:x4}",
                            Target = target,
                            CallbackType = known ? callback.DeclaringType.FullName.Replace('/', '+') : null,
                            CallbackName = known ? callback.Name : null,
                            Startup = startup,
                        });
                    }
                    else if (callsMethod && callee.DeclaringType.Scope == module
                        && pluginMethods.TryGetValue(callee.FullName, out var nested))
                        Visit(nested, startup);
                }
            }

            var entry = module.GetType("RainMeadow.RainMeadow");
            foreach (var name in new[] { "OnEnable", "RainWorld_OnModsInit" })
            {
                var method = entry.Methods.SingleOrDefault(candidate => candidate.Name == name);
                if (method != null)
                    Visit(method, true);
            }
            foreach (var method in pluginMethods.Values)
                Visit(method, false);
            return registrations;
        }
    }

    private static void RedirectUnityLogging(ModuleDefinition module)
    {
        var customLogs = new HashSet<string> { "Log", "LogWarning", "LogImportant" };
        foreach (var method in module.Types.SelectMany(NestedTypes).SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference call && call.DeclaringType.FullName == "RWCustom.Custom"
                    && customLogs.Contains(call.Name) && call.Parameters.Count == 1 && !call.HasThis
                    && call.ReturnType.FullName == "System.Void")
                {
                    instruction.OpCode = OpCodes.Pop;
                    instruction.Operand = null;
                }
            }
        }
    }

    private static Assembly LoadGameFixture(string managed, out Dictionary<string, int> methodTokens)
    {
        using (var resolver = new DefaultAssemblyResolver())
        using (var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll"),
            new ReaderParameters { AssemblyResolver = resolver }))
        using (var image = new MemoryStream())
        {
            resolver.AddSearchDirectory(Path.GetFullPath(managed));
            // The real initializer calls Unity. The tested method bodies are left intact.
            foreach (var owner in new[] { "RWCustom.Custom", "RainWorld" })
            {
                var initializer = game.GetType(owner).Methods.Single(method => method.Name == ".cctor");
                initializer.Body = new Mono.Cecil.Cil.MethodBody(initializer);
                initializer.Body.GetILProcessor().Emit(OpCodes.Ret);
            }
            game.Write(image);
            methodTokens = MethodIndex(game).ToDictionary(pair => pair.Key, pair => pair.Value.MetadataToken.ToInt32());
            return Assembly.Load(image.ToArray());
        }
    }

    private static string JitFailure(DynamicMethodDefinition method)
    {
        try
        {
            return DetourHelper.GetNativeStart(method.Generate()) == IntPtr.Zero ? "null native entry point" : null;
        }
        catch (Exception error)
        {
            return error.GetType().Name + ": " + error.Message.Trim();
        }
    }

    private static string Flatten(string text) => (text ?? "").Replace("\r", "").Replace("\n", " | ").Replace("\t", " ");

    private static int CheckHookChains(string managed, string plugin, string dependencies, string report)
    {
        var registrations = RegistrationOrder(plugin, managed);
        if (registrations.Count == 0)
            throw new InvalidOperationException("No managed IL registrations found in the plugin");

        var assembly = LoadProbe(plugin, managed, dependencies, batch: true);
        var game = LoadGameFixture(managed, out var methodTokens);
        bool failed = false;
        using (var output = new StreamWriter(report))
        {
            output.AutoFlush = true;
            output.WriteLine("scope\ttarget\tcallback\tstatus\tdetail\tlocation");
            void Record(Registration row, string callback, string status, string detail)
            {
                if (status != "compiled" && status != "anchor-error")
                    failed = true;
                output.WriteLine(string.Join("\t", row.Startup ? "startup" : "other", row.Target, callback,
                    status, Flatten(detail), row.Location));
            }

            foreach (var bad in registrations.Where(row => row.Target == null || row.CallbackName == null))
                Record(bad, "", "unmapped", "Could not resolve the delegate or target");

            foreach (var group in registrations.Where(row => row.Target != null && row.CallbackName != null).GroupBy(row => row.Target))
            {
                if (!methodTokens.TryGetValue(group.Key, out int token))
                {
                    foreach (var row in group)
                        Record(row, "", "target-missing", "");
                    continue;
                }

                var original = game.ManifestModule.ResolveMethod(token);
                using (var dmd = new DynamicMethodDefinition(original))
                {
                    if (dmd.Definition.Module.AssemblyResolver is BaseAssemblyResolver resolver)
                    {
                        resolver.AddSearchDirectory(Path.GetFullPath(managed));
                        resolver.AddSearchDirectory(Path.GetFullPath(dependencies));
                    }
                    var contexts = new List<ILContext>();
                    try
                    {
                        var baseline = JitFailure(dmd);
                        bool previousFailed = false;
                        foreach (var row in group)
                        {
                            var name = row.CallbackType + "::" + row.CallbackName;
                            if (previousFailed)
                            {
                                Record(row, name, "upstream-hook-failed", "");
                                continue;
                            }
                            var captured = new StringWriter();
                            var stdout = Console.Out;
                            try
                            {
                                Console.SetOut(captured);
                                var owner = assembly.GetType(row.CallbackType, throwOnError: true);
                                var callback = owner.GetMethod(row.CallbackName,
                                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new[] { typeof(ILContext) }, null);
                                if (callback == null)
                                    throw new MissingMethodException(row.CallbackType, row.CallbackName);
                                var instance = callback.IsStatic ? null : FormatterServices.GetUninitializedObject(callback.DeclaringType);
                                if (instance != null)
                                    AttachProbeLogger(instance, callback.DeclaringType);
                                var context = new ILContext(dmd.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance };
                                contexts.Add(context);
                                context.Invoke(il => callback.Invoke(instance, new object[] { il }));
                                var error = JitFailure(dmd);
                                var log = captured.ToString().Trim();
                                var status = error == null ? "compiled" : baseline == null ? "jit-failed" : "host-limited";
                                if (error == null && log.Contains("Exception:"))
                                    status = "anchor-error";
                                Record(row, name, status, error ?? log);
                                previousFailed = status == "jit-failed";
                            }
                            catch (Exception error)
                            {
                                Record(row, name, "callback-error", (error.InnerException ?? error).ToString());
                                previousFailed = true;
                            }
                            finally
                            {
                                Console.SetOut(stdout);
                            }
                        }
                    }
                    finally
                    {
                        foreach (var context in contexts)
                            context.Dispose();
                    }
                }
            }
        }
        return failed ? 1 : 0;
    }
}
