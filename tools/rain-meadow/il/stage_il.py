"""Stage IL cursor changes verified against the local Android assembly."""

import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CREATURE = Path("Game/RainMeadow.CreatureHooks.cs")
COSMETIC = Path("Game/CustomCosmetics/RainMeadow.CustomCosmeticHooks.cs")
GAMEPLAY = Path("Game/RainMeadow.GameplayHooks.cs")
ARENA = Path("Arena/ArenaHooks.cs")
GAME = Path("Game/RainMeadow.GameHooks.cs")
PLAYER = Path("Game/RainMeadow.PlayerHooks.cs")
DEATH_CONTEXT = Path("Game/DeathContextualizer.cs")
DROWN = Path("Arena/ArenaOnlineGameModes/Drown/DrownHooks.cs")
STORY = Path("Story/StoryHooks.cs")

RULES = {
    DROWN: (
        (
            """                ILLabel skip = il.DefineLabel();
                c.GotoNext(
                     i => i.MatchLdloc(18),
                     i => i.MatchIsinst<Creature>(),
                     i => i.MatchCallvirt<Creature>("Die")
                     );
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, 18);""",
            """                int targetLocal = -1;
                c.GotoNext(
                    i => i.MatchLdloc(out targetLocal)
                        && il.Body.Variables[targetLocal].VariableType.FullName == nameof(PhysicalObject),
                    i => i.MatchIsinst<Creature>(),
                    i => i.MatchCallvirt<Creature>(nameof(Creature.Die)));
                // Leave the branch for already-dead targets attached to the vanilla kill path.
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, il.Body.Variables[targetLocal]);""",
        ),
    ),
    STORY: (
        (
            """                // remote don't spawn SLOracleSwarmer during GoToOracle phase
                var c = new ILCursor(il);
                var skip = il.DefineLabel();""",
            """                // Only the owner spawns the replacement swarmer during GoToOracle.
                var c = new ILCursor(il);
                int swarmerLocal = -1;""",
        ),
        (
            """                    i => i.MatchNewobj<SLOracleSwarmer>(),
                    i => i.MatchStloc(4)
                );
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate((SLOracleWakeUpProcedure wakeUpProcedure) => wakeUpProcedure.SLOracle?.IsLocal() ?? true);
                c.Emit(OpCodes.Brfalse, skip);
                c.GotoNext(MoveType.After,
                    i => i.MatchLdarg(0),
                    i => i.MatchLdfld<SLOracleWakeUpProcedure>("SLOracle"),
                    i => i.MatchLdfld<Oracle>("mySwarmers"),
                    i => i.MatchLdloc(4),
                    i => i.MatchCallvirt("System.Collections.Generic.List`1<OracleSwarmer>", "Add")
                );
                c.MarkLabel(skip);""",
            """                    i => i.MatchNewobj<SLOracleSwarmer>(),
                    i => i.MatchStloc(out swarmerLocal)
                        && il.Body.Variables[swarmerLocal].VariableType.FullName == nameof(SLOracleSwarmer)
                );
                // Find the end of the creation block before editing any instructions.
                var end = new ILCursor(il) { Index = c.Index };
                end.GotoNext(MoveType.After,
                    i => i.MatchLdarg(0),
                    i => i.MatchLdfld<SLOracleWakeUpProcedure>("SLOracle"),
                    i => i.MatchLdfld<Oracle>("mySwarmers"),
                    i => i.MatchLdloc(swarmerLocal),
                    i => i.MatchCallvirt("System.Collections.Generic.List`1<OracleSwarmer>", "Add")
                );
                var skip = il.DefineLabel();
                end.MarkLabel(skip);
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate((SLOracleWakeUpProcedure wakeUpProcedure) => wakeUpProcedure.SLOracle?.IsLocal() ?? true);
                c.Emit(OpCodes.Brfalse, skip);""",
        ),
    ),
    DEATH_CONTEXT: (
        (
            """                    // Make a skip label
                    var skip = il.DefineLabel();""",
            """                    // Keep any incoming branches attached to the replacement sequence.
                    c.MoveAfterLabels();""",
        ),
        (
            """                    c.Emit(OpCodes.Br, skip);
                    c.GotoNext(moveType: MoveType.After,
                        i => i.MatchCallOrCallvirt<Creature>(nameof(Creature.Violence))
                    );
                    c.MarkLabel(skip);""",
            """                    // The delegate already calls Violence. Remove the displaced call.
                    c.Remove();""",
        ),
    ),
    CREATURE: (
        (
            'x => x.MatchCall(typeof(ExtEnum<MoreSlugcats.StowawayBugAI.Behavior>).GetMethod("op_Equality")),',
            '''x => x.OpCode == OpCodes.Call
                    && x.Operand is Mono.Cecil.MethodReference { Name: "op_Equality", DeclaringType: Mono.Cecil.GenericInstanceType owner }
                    && owner.ElementType.FullName == "ExtEnum`1"
                    && owner.GenericArguments.Count == 1
                    && owner.GenericArguments[0].FullName == "MoreSlugcats.StowawayBugAI/Behavior",''',
        ),
        (
            '''x => x.MatchCallvirt(typeof(MoreSlugcats.StowawayBugGraphics).GetMethod(nameof(MoreSlugcats.StowawayBugGraphics.Bite))),
                x => x.MatchStloc(15)''',
            '''x => x.MatchCallvirt<MoreSlugcats.StowawayBugGraphics>(nameof(MoreSlugcats.StowawayBugGraphics.Bite))''',
        ),
        (
            '''// if ((base.grasps[num4].grabbed as global::Creature).dead)

            c.GotoNext(MoveType.After,
                x => x.MatchLdarg(0),
                x => x.MatchCall<Creature>("get_grasps"),
                x => x.MatchLdloc(17),''',
            '''// if ((base.grasps[num4].grabbed as global::Creature).dead)

            c.GotoNext(MoveType.After,
                x => x.MatchLdarg(0),
                x => x.MatchCall<Creature>("get_grasps"),
                x => x.MatchLdloc(out _),''',
        ),
    ),
    GAMEPLAY: (
        (
            '''                ILLabel label = cursor.DefineLabel();
                if (cursor.TryGotoNext(MoveType.After, x => x.MatchBneUn(out label)))
                {
                    cursor.MoveAfterLabels();
                    cursor.EmitDelegate(() => OnlineManager.lobby is not null && RPCEvent.currentRPCEvent is not null);
                    cursor.Emit(OpCodes.Brtrue, label);\x20
                    // If we are in an RPC, move it directly to parrying, even if the weapons are going in the same directions

                    cursor.GotoLabel(label, MoveType.Before);''',
            '''                ILLabel label = cursor.DefineLabel();
                if (cursor.TryGotoNext(MoveType.After, x => x.MatchBneUn(out label)))
                {
                    cursor.MoveAfterLabels();
                    cursor.EmitDelegate(() => OnlineManager.lobby is not null && RPCEvent.currentRPCEvent is not null);
                    cursor.Emit(OpCodes.Brtrue, label);
                    cursor.GotoLabel(label, MoveType.Before);
                }
                else if (cursor.TryGotoNext(MoveType.Before,
                    x => x.MatchLdcR4(0f),
                    x => x.MatchClt(),
                    x => x.MatchBeq(out label)) && label.Target.OpCode == OpCodes.Ret)
                {
                    // Android emits a same-direction early return. Preserve that result unless handling an RPC.
                    cursor.Index += 2;
                    cursor.Remove();
                    cursor.Emit(OpCodes.Ceq);
                    cursor.EmitDelegate((bool sameDirection) =>
                        sameDirection && !(OnlineManager.lobby is not null && RPCEvent.currentRPCEvent is not null));
                    cursor.Emit(OpCodes.Brtrue, label);
                }
                else
                {
                    RainMeadow.Error("Could not identify the weapon parry direction branch");
                    return;
                }

                {''',
        ),
        (
            '''                else
                {
                    RainMeadow.Error("Could not find IL hook :<");
                }''',
            "",
        ),
    ),
    ARENA: (
        (
            """                    // Make a skip label
                    var skip = il.DefineLabel();

                    // Get caller typ""",
            """                    // Keep incoming branches attached to the replacement sequence.
                    c.MoveAfterLabels();

                    // Get caller type""",
        ),
        (
            """                    c.Emit(OpCodes.Br, skip);
                    c.GotoNext(
                        moveType: MoveType.After,
                        i => i.MatchCallOrCallvirt<Creature>(nameof(Creature.Violence))
                    );
                    c.MarkLabel(skip);""",
            """                    // The delegate already calls Violence.
                    c.Remove();""",
        ),
        (
            """                // --- 1. Proximity & slow down logic ---
                cursor.GotoNext(MoveType.After, x => x.MatchBneUn(out label));
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldloc, 4);""",
            """                // --- 1. Proximity & slow down logic ---
                int spawnIndex = -1;
                cursor.GotoNext(MoveType.After,
                    x => x.MatchLdfld<Room>(nameof(Room.voidSpawns)),
                    x => x.MatchLdloc(out spawnIndex)
                        && il.Body.Variables[spawnIndex].VariableType.MetadataType == Mono.Cecil.MetadataType.Int32,
                    x => x.OpCode == OpCodes.Callvirt
                        && x.Operand is Mono.Cecil.MethodReference { Name: "get_Item", DeclaringType: Mono.Cecil.GenericInstanceType list }
                        && list.FullName == "System.Collections.Generic.List`1<VoidSpawn>",
                    x => x.MatchLdfld<PhysicalObject>(nameof(PhysicalObject.abstractPhysicalObject)),
                    x => x.MatchLdfld<AbstractPhysicalObject>(nameof(AbstractPhysicalObject.rippleLayer)),
                    x => x.MatchLdarg(0),
                    x => x.MatchCallOrCallvirt<Creature>("get_abstractCreature"),
                    x => x.MatchLdfld<AbstractPhysicalObject>(nameof(AbstractPhysicalObject.rippleLayer)),
                    x => x.MatchBneUn(out label));
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldloc, il.Body.Variables[spawnIndex]);""",
        ),
        (
            '''                // Change the throw detection value
                if (!cursor.TryGotoNext(moveType: MoveType.After,''',
            '''                // Locate the weapon local before checking the throw detection range.
                int weaponLocal = -1;
                if (!cursor.TryGotoNext(moveType: MoveType.After,
                    x => x.MatchLdloc(out weaponLocal)
                        && il.Body.Variables[weaponLocal].VariableType.FullName == nameof(Weapon),''',
        ),
        (
            '''                cursor.Emit(OpCodes.Ldloc, 20);
                cursor.EmitDelegate((Player player, Weapon weapon)''',
            '''                cursor.Emit(OpCodes.Ldloc, il.Body.Variables[weaponLocal]);
                cursor.EmitDelegate((Player player, Weapon weapon)''',
        ),
        (
            '''                    x => x.MatchStloc(21))
                ) { throw new KeyNotFoundException("Couldn't find the key 9 of IL hook"); }''',
            '''                    x => x.MatchIsinst<Creature>(),
                    x => x.MatchStloc(out _))
                ) { throw new KeyNotFoundException("Couldn't find the Artificer stun target local"); }''',
        ),
        (
            '''                    x => x.MatchLdsfld(typeof(DLCSharedEnums.GameTypeID), nameof(DLCSharedEnums.GameTypeID.Challenge)),
                    x => x.MatchCall(typeof(ExtEnum<ArenaSetup.GameTypeID>).GetMethod("op_Equality")));''',
            '''                    x => x.MatchLdsfld(typeof(DLCSharedEnums.GameTypeID), nameof(DLCSharedEnums.GameTypeID.Challenge)),
                    x => x.OpCode == OpCodes.Call
                        && x.Operand is Mono.Cecil.MethodReference { Name: "op_Equality", DeclaringType: Mono.Cecil.GenericInstanceType owner }
                        && owner.FullName == "ExtEnum`1<ArenaSetup/GameTypeID>");''',
        ),
    ),
    GAME: (
        (
            '''                var skip2 = il.DefineLabel();
                c.GotoNext(
                    i => i.MatchLdloc(0),''',
            '''                var skip2 = il.DefineLabel();
                int pauseButton = -1;
                c.GotoNext(MoveType.After,
                    i => i.MatchCall(typeof(RWInput), nameof(RWInput.CheckPauseButton)),
                    i => i.MatchStloc(out pauseButton));
                c.GotoNext(
                    i => i.MatchLdloc(pauseButton),''',
        ),
        (
            '''                    i => i.MatchLdloc(29),
                    i => i.MatchCallvirt(typeof(List<PlacedObject>).GetMethod("get_Item")),
                    i => i.MatchCall<Room>(nameof(Room.SpawnPrinceBulb))''',
            '''                    i => i.MatchLdloc(out _),
                    i => i.OpCode == OpCodes.Callvirt
                        && i.Operand is Mono.Cecil.MethodReference { Name: "get_Item", DeclaringType: Mono.Cecil.GenericInstanceType list }
                        && list.FullName == "System.Collections.Generic.List`1<PlacedObject>",
                    i => i.MatchCall<Room>(nameof(Room.SpawnPrinceBulb))''',
        ),
    ),
    PLAYER: (
        (
            '''            ILLabel skipDevTools = il.DefineLabel();
            c.GotoNext(MoveType.After,
                i => i.MatchLdstr("v"),
                i => i.MatchCallOrCallvirt<UnityEngine.Input>(nameof(UnityEngine.Input.GetKey)),
                i => i.MatchLdloc(33),''',
            '''            ILLabel skipDevTools = il.DefineLabel();
            int devToolsLocal = -1;
            c.GotoNext(MoveType.After,
                i => i.MatchLdstr("v"),
                i => i.MatchCallOrCallvirt<UnityEngine.Input>(nameof(UnityEngine.Input.GetKey)),
                i => i.MatchLdloc(out devToolsLocal)
                    && il.Body.Variables[devToolsLocal].VariableType.MetadataType == Mono.Cecil.MetadataType.Boolean,''',
        ),
        (
            '''                i => i.MatchLdstr("w"),
                i => i.MatchCallOrCallvirt<UnityEngine.Input>(nameof(UnityEngine.Input.GetKey)),
                i => i.MatchLdloc(33),''',
            '''                i => i.MatchLdstr("w"),
                i => i.MatchCallOrCallvirt<UnityEngine.Input>(nameof(UnityEngine.Input.GetKey)),
                i => i.MatchLdloc(devToolsLocal),''',
        ),
        (
            '''                i => i.MatchCallvirt<Room>("get_abstractRoom"),
                i => i.MatchLdloc(19),
                i => i.MatchCallvirt<AbstractRoom>("AddEntity")''',
            '''                i => i.MatchCallvirt<Room>("get_abstractRoom"),
                i => i.MatchLdloc(loc),
                i => i.MatchCallvirt<AbstractRoom>("AddEntity")''',
        ),
        (
            '''            c.Emit(OpCodes.Ldloc, 19);
            c.EmitDelegate((int freehand, Player p, AbstractSpear spear) =>''',
            '''            c.Emit(OpCodes.Ldloc, loc);
            c.EmitDelegate((int freehand, Player p, AbstractSpear spear) =>''',
        ),
        (
            '''                i => i.MatchLdloc(16),
                i => i.MatchLdfld<PlayerGraphics.TailSpeckles>("spearProg"),''',
            '''                i => i.MatchLdloc(out _),
                i => i.MatchLdfld<PlayerGraphics.TailSpeckles>("spearProg"),''',
        ),
    ),
    COSMETIC: (
        (
            '''                cursor.GotoNext(MoveType.After,
                    x => x.MatchStloc(7),
                    x => x.MatchBr(out _)
                );
                cursor.MoveAfterLabels();
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldloc_2);
                cursor.Emit(OpCodes.Ldloc_S, (byte)7);
                cursor.Emit(OpCodes.Ldloc_0);''',
            '''                int itemIndex = -1;
                int updateListIndex = -1;
                cursor.GotoNext(MoveType.After,
                    x => x.MatchLdloc(out updateListIndex)
                        && il.Body.Variables[updateListIndex].VariableType.FullName
                            == "System.Collections.Generic.List`1<UpdatableAndDeletable>",
                    x => x.MatchLdloc(out itemIndex)
                        && il.Body.Variables[itemIndex].VariableType.MetadataType == Mono.Cecil.MetadataType.Int32,
                    x => x.OpCode == OpCodes.Callvirt
                        && x.Operand is Mono.Cecil.MethodReference { Name: "get_Item", DeclaringType: Mono.Cecil.GenericInstanceType owner }
                        && owner.FullName == "System.Collections.Generic.List`1<UpdatableAndDeletable>",
                    x => x.MatchLdloc(out _),
                    x => x.MatchCall<WindRect>(nameof(WindRect.WindAffectsObject)),
                    x => x.MatchBrfalse(out _)
                );
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldloc, il.Body.Variables[updateListIndex]);
                cursor.Emit(OpCodes.Ldloc, il.Body.Variables[itemIndex]);
                cursor.Emit(OpCodes.Ldloc_0);''',
        ),
    ),
}


def inside(value):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT):
        raise argparse.ArgumentTypeError(f"Path must be in the workspace: {value}")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=inside)
    parser.add_argument("output", type=inside)
    args = parser.parse_args()
    if args.output.exists() or args.output.is_relative_to(args.source):
        parser.error("Choose a new output path outside the upstream source tree")
    updates = {}
    for relative, rules in RULES.items():
        source = args.source / relative
        data = source.read_bytes()
        newline = b"\r\n" if b"\r\n" in data else b"\n"
        for old, new in rules:
            old = old.encode().replace(b"\n", newline)
            new = new.encode().replace(b"\n", newline)
            if data.count(old) != 1:
                parser.error(f"Expected one anchor in {relative}: {old[:85]!r}")
            data = data.replace(old, new)
        updates[relative] = data

    for relative, data in updates.items():
        destination = args.output / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        print(destination.relative_to(ROOT))


if __name__ == "__main__":
    main()
