using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Rpc;
using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Serialization;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     `--traps-selftest`: the parts of trap placement that can be wrong without anything saying so.
///
///     * the generated table: every context item resolves to a real per-surface item with an actor
///       to spawn, and every tool is one whose ClassNetCache chain is known (an unknown one decodes
///       the placement RPC with the wrong field width, silently);
///     * the tool's C# TYPE: SpawnActor makes whatever the UClass says, so a trap tool resolved
///       as a plain AFortWeapon would replicate the weapon layout and never send ItemDefinition;
///     * the placement RPC's decode, including the 4-bit enum at the end, from a bunch written the
///       way the client writes it.
/// </summary>
public static class FortTrapsSelfTest {
    private sealed class TestWorld : UWorld { }

    private static int _failures;

    private static void Check(bool ok, string what) {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    public static bool RunSelfTest() {
        _failures = 0;
        Console.WriteLine("FortTrapsSelfTest:");

        // ---------------------------------------------------------------- the table
        string[] brItems = {
            "TID_Floor_Player_Launch_Pad_Athena", "TID_Context_BouncePad_Athena", "TID_Floor_Player_Campfire_Athena",
            "TID_ContextTrap_Athena", "TID_PoisonDartTrap_Context", "TID_Context_Freeze_Athena",
            "TID_Floor_MountedTurret_Athena", "TID_ZippyTroutTrap_Context"
        };

        foreach (var name in brItems) {
            var def = FortTraps.ForPath("/x." + name);
            Check(def is { ToolClass: not null, ActorClass: not null }, $"{name} has a tool and an actor");
            if (def is not { IsContext: true }) continue;

            foreach (var surface in new[] { EBuildingAttachmentType.ATTACH_Floor, EBuildingAttachmentType.ATTACH_Wall, EBuildingAttachmentType.ATTACH_Ceiling }) {
                // A surface the item names nothing for is legitimate - the bouncer has no ceiling
                // variant in Battle Royale - and must come back null, not the context item.
                var listed = surface switch {
                    EBuildingAttachmentType.ATTACH_Wall => def.WallTrap,
                    EBuildingAttachmentType.ATTACH_Ceiling => def.CeilingTrap,
                    _ => def.FloorTrap
                };
                var placed = FortTraps.PlacedFor(def, surface);
                if (listed == null) {
                    Check(placed == null, $"{name} on {surface} places nothing (none listed)");
                    continue;
                }

                Check(placed is { ActorClass: not null, IsContext: false },
                      $"{name} on {surface} places {placed?.ActorClass?[(placed.ActorClass.LastIndexOf('.') + 1)..] ?? "NOTHING"}");
            }
        }

        var knownTools = new[] { "TrapTool_C", "TrapTool_ContextTrap_Athena_C" };
        foreach (var name in brItems) {
            var tool = FortTraps.ForPath("/x." + name)?.ToolClass;
            var toolName = tool?[(tool.LastIndexOf('.') + 1)..];
            Check(toolName != null && knownTools.Contains(toolName), $"{name}'s tool {toolName} has a known net-field chain");
        }

        // ---------------------------------------------------------------- the tool's type
        var world = new TestWorld();
        var launchPad = UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Traps/TID_Floor_Player_Launch_Pad_Athena.TID_Floor_Player_Launch_Pad_Athena");
        var toolClass = FortWeaponActorClasses.ClassFor(launchPad);
        Check(toolClass != null, "a launch pad resolves to a tool class");
        if (toolClass != null) {
            var tool = world.SpawnActor<AFortWeapon>(toolClass, new FActorSpawnParameters());
            Check(tool is AFortDecoTool, $"...and spawns as AFortDecoTool (got {tool?.GetType().Name})");
            Check(tool != null && ReferenceEquals(NativeRepLayouts.Get(tool), NativeRepLayouts.DecoTool),
                  "...which replicates with the deco-tool layout");
            Check(tool != null && NativeRpcHandlers.Get(tool)?.ContainsKey("ServerSpawnDeco") == true,
                  "...and has the placement RPCs");
        }

        var rifle = UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_R_Ore_T03.WID_Assault_Auto_Athena_R_Ore_T03");
        var rifleClass = FortWeaponActorClasses.ClassFor(rifle);
        Check(rifleClass != null && world.SpawnActor<AFortWeapon>(rifleClass, new FActorSpawnParameters()) is not AFortDecoTool,
              "a rifle is still a plain AFortWeapon");

        // ---------------------------------------------------------------- behaviours
        // Every behaviour must be keyed on a class the table can actually place - a typo here is a
        // trap that silently does nothing.
        var placeable = FortTraps.PlaceableActorClasses.Select(path => path[(path.LastIndexOf('.') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var className in FortTraps.BehaviourClassNames) {
            Check(placeable.Contains(className), $"behaviour {className} names a placeable trap class");
        }

        // The trigger box follows the trap's rotation. Floor spikes: box centre at local (0,256).
        var spikes = FortTraps.BehaviourFor("x.Trap_Athena_Spikes_C")!;
        var floorTrap = world.SpawnActor<ABuildingTrap>(
            GUClassArray.StaticClassForPath<ABuildingTrap>("/Game/Items/Traps/Blueprints/Athena/Trap_Athena_Spikes.Trap_Athena_Spikes_C"),
            new FActorSpawnParameters());
        floorTrap.SetActorLocation(new FVector { X = 1000f, Y = 2000f, Z = 0f });
        floorTrap.SetActorRotation(new FRotator());
        Check(FortTrapSystem.Overlaps(floorTrap, spikes, new FVector { X = 1000f, Y = 2256f, Z = 96f }),
              "yaw 0: a player standing on the tile's middle is inside");
        Check(!FortTrapSystem.Overlaps(floorTrap, spikes, new FVector { X = 1000f, Y = 1700f, Z = 96f }),
              "yaw 0: a player on the next tile over is not");
        Check(!FortTrapSystem.Overlaps(floorTrap, spikes, new FVector { X = 1000f, Y = 2256f, Z = 600f }),
              "yaw 0: a player on the floor above is not");

        floorTrap.SetActorRotation(new FRotator { Yaw = 90f });
        Check(FortTrapSystem.Overlaps(floorTrap, spikes, new FVector { X = 744f, Y = 2000f, Z = 96f }),
              "yaw 90: the box turned with the trap (local +Y is world -X)");
        Check(!FortTrapSystem.Overlaps(floorTrap, spikes, new FVector { X = 1000f, Y = 2256f, Z = 96f }),
              "yaw 90: ...and is no longer where it was at yaw 0");

        // The trap's ability system IS the client's own constructed one: stably named, by the
        // exact names its demo recorder prints. A dynamic one is a second component nobody
        // listens to - round 3's "never armed". See ABuildingTrap.HasNativeAbilitySubobjects.
        var trapAsc = floorTrap.EnsureAbilitySystemComponent(withAttributeSet: true);
        Check(trapAsc is { } && trapAsc.IsNameStableForNetworking() && trapAsc.GetFName().ToString() == "AbilitySystemComponent",
              $"a trap's ASC is the stably named AbilitySystemComponent (got {trapAsc?.GetFName()}, stable {trapAsc?.IsNameStableForNetworking()})");
        Check(floorTrap.BuildingAttributeSet is { } trapSet && trapSet.IsNameStableForNetworking() &&
              trapSet.GetFName().ToString() == "BuildingAttributeSet",
              "...its BuildingAttributeSet is stably named too");
        var setNames = trapAsc?.SpawnedAttributes.Select(set => set.GetFName().ToString()).ToArray() ?? [];
        Check(setNames.SequenceEqual(["BuildingAttributeSet", "TrapDamageAttributeSet"]),
              $"...and SpawnedAttributes lists both constructed sets ({string.Join(", ", setNames)})");

        var pbwaWall = world.SpawnActor<ABuildingActor>(
            GUClassArray.StaticClassForPath<ABuildingActor>("/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Solid.PBWA_W1_Solid_C"),
            new FActorSpawnParameters());
        var wallAsc = pbwaWall?.EnsureAbilitySystemComponent(withAttributeSet: true);
        Check(wallAsc is { } && !wallAsc.IsNameStableForNetworking(),
              "a PBWA piece's ASC stays dynamic - its class constructs none on the client");

        // The bouncers' numbers.
        var up = new FVector { Z = 1f };
        var still = FortTrapSystem.FloorBounceVelocity(new FVector(), up);
        Check(MathF.Abs(still.Z - 1600f) < 0.5f && MathF.Abs(still.X) < 0.01f, $"floor bouncer from rest: straight up at 1600 (got {still.Z:F1})");
        var running = FortTrapSystem.FloorBounceVelocity(new FVector { X = 2000f, Z = -1200f }, up);
        var runningSize = MathF.Sqrt(running.X * running.X + running.Y * running.Y + running.Z * running.Z);
        Check(MathF.Abs(runningSize - 1600f) < 0.5f && running.X > 0f && running.Z > 0f,
              $"floor bouncer at a run: still exactly 1600, carried forward ({running.X:F0}, {running.Z:F0})");
        var wallBounce = FortTrapSystem.WallBounceVelocity(new FVector { Y = 1f });
        Check(MathF.Abs(wallBounce.Y - 1600f) < 0.5f && MathF.Abs(wallBounce.Z - 800f) < 0.5f, "wall bouncer: 1600 out, 800 up");

        // ---------------------------------------------------------------- the RPC decode
        var rpcs = NativeRpcHandlers.Get(new AFortDecoTool())!;

        // ServerSpawnDeco(Location, Rotation, AttachedActor = null, ATTACH_Wall): one presence bit per
        // non-bool parameter, set only when the value is not its default.
        var writer = new FBitWriter(1024, true);
        writer.WriteBit(true);
        new FVector { X = 100f, Y = -200f, Z = 300.5f }.NetSerializeWrite(writer);
        writer.WriteBit(true);
        new FRotator { Yaw = 90f }.NetSerializeWrite(writer);
        writer.WriteBit(false);                                      // AttachedActor: null
        writer.WriteBit(true);
        var wall = new byte[] { (byte) EBuildingAttachmentType.ATTACH_Wall };
        writer.SerializeBits(wall, 4);
        writer.WriteBit(true);                                       // a trailing marker: nothing over-read

        var reader = new FBitReader(writer.GetData(), (int) writer.GetNumBits());
        var values = FRpcReader.ReadParams(reader, rpcs["ServerSpawnDeco"].Params);
        var location = values[0] as FVector;
        Check(location is { X: 100f, Y: -200f, Z: 300.5f }, $"ServerSpawnDeco Location ({location?.X}, {location?.Y}, {location?.Z})");
        Check(values[1] is FRotator { Yaw: 90f }, "ServerSpawnDeco Rotation yaw 90");
        Check(values[2] == null, "ServerSpawnDeco AttachedActor absent");
        Check(values[3] is byte b && b == (byte) EBuildingAttachmentType.ATTACH_Wall, $"ServerSpawnDeco surface = ATTACH_Wall (got {values[3]})");
        var afterEnum = reader.GetPosBits();
        var marker = reader.ReadBit();
        Check(marker && reader.GetPosBits() == writer.GetNumBits(),
              $"the enum is exactly 4 bits (read to {afterEnum} of {writer.GetNumBits()}, marker {marker})");

        // THE READER'S LAST BITS, for every length within a byte. FBitReader(byte[], num) masks the
        // bits past num; it once masked the last VALID bits too (see FBitReader.ApplyMask).
        var tailOk = true;
        for (var bits = 1; bits <= 16; bits++) {
            var all = new byte[] { 0xFF, 0xFF };
            var tail = new FBitReader(all, bits);
            for (var i = 0; i < bits; i++) tailOk &= tail.ReadBit();
        }
        Check(tailOk, "a reader of any length reads its own last bits as sent");

        // An absent surface is ATTACH_Floor - the zero value.
        writer = new FBitWriter(1024, true);
        writer.WriteBit(false);
        writer.WriteBit(false);
        writer.WriteBit(false);
        writer.WriteBit(false);
        reader = new FBitReader(writer.GetData(), (int) writer.GetNumBits());
        values = FRpcReader.ReadParams(reader, rpcs["ServerSpawnDeco"].Params);
        Check(values.All(v => v == null), "four clear presence bits read as four defaults");

        Console.WriteLine(_failures == 0 ? "FortTrapsSelfTest: all passed" : $"FortTrapsSelfTest: {_failures} FAILED");
        return _failures == 0;
    }
}
