using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Publicize
{
    private static void MakePublic(TypeDefinition type)
    {
        if (type.Name == "<Module>")
            return;

        if (type.IsNested)
            type.IsNestedPublic = true;
        else
            type.IsPublic = true;

        foreach (var field in type.Fields)
        {
            // Exposing a private event backing field creates an ambiguous C# symbol.
            if (!type.Events.Any(item => item.Name == field.Name))
                field.IsPublic = true;

            // APIs Meadow writes through its PUBLIC-Assembly-CSharp build reference.
            var ownerAndField = type.FullName + "::" + field.Name;
            switch (ownerAndField)
            {
                case "Player::enterIntoCamoDuration":
                case "ConfigurableBase::OI":
                case "Menu.Remix.MixedUI.OpTextBox::accept":
                case "Menu.Remix.MixedUI.ListItem::name":
                case "Menu.Remix.MixedUI.ListItem::value":
                    field.IsInitOnly = false;
                    break;
            }
        }
        foreach (var method in type.Methods)
            method.IsPublic = true;
        foreach (var nested in type.NestedTypes)
            MakePublic(nested);
    }

    private static void Main(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: Publicize INPUT OUTPUT");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        using (var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver }))
        {
            foreach (var type in module.Types)
            {
                // Exclude the embedded shim from C# name lookup. Plugins use the separate BepInEx.dll.
                if (type.Namespace == "BepInEx" || type.Namespace.StartsWith("BepInEx.", StringComparison.Ordinal))
                    type.Namespace = "EmbeddedPortShim." + type.Namespace;
                MakePublic(type);
            }
            module.Write(args[1]);
        }
    }
}
