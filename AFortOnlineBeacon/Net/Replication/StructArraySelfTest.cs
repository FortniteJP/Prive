using AFortOnlineBeacon.Serialization;

namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     Round-trips <see cref="ERepPropertyKind.StructArray" /> through this project's own bit writer
///     and reads the handles back, checking them against the numbers RepLayout.cpp's own arithmetic
///     produces.
///
///     WHY IT NEEDS TO EXIST. A wrong handle inside an array is not a wrong value. The client's
///     ReceiveProperties_r walks one continuous handle counter across every element's cmds, and a
///     number it does not expect ends the parse - `ReceiveProperties_r: Failed to receive property,
///     Array Property Improperly Terminated` - which closes the connection, taking driving down with
///     it. There is no partial failure to notice in-game and no way to see it coming from the server
///     side, so the arithmetic is checked here instead.
///
///     What it does NOT check: whether the table has the right number of children for a real struct.
///     That is a question about the SDK, and `python Tools/RepHandles/verify_cs_handles.py` answers
///     it directly. The two together cover the whole derivation - the count from the SDK, the
///     arithmetic from here.
///
///     Deliberately uses a synthetic Float member rather than the real seat array's ObjectRef: an
///     object reference needs a live UPackageMapClient and a connection, and the thing under test is
///     the handle stream, which is identical either way.
/// </summary>
public static class StructArraySelfTest {
    /// <summary>One synthetic array element - a mutable box, so the writer's getters have something to read.</summary>
    private sealed class Element {
        public float Value { get; init; }
    }

    private sealed class Holder {
        public List<object> Elements { get; } = new();
    }

    public static bool RunSelfTest() {
        var failures = 0;

        // The same shape as the seats: a wide element (31 members) of which exactly one is modelled.
        // 31 is not arbitrary here - it is FAthenaCarPlayerSlot's real width, so the numbers this
        // prints are the ones a real seat push carries.
        const int handlesPerElement = 31;
        const int modelledChild = 24;                 // 1-based, as handles are - Player's slot

        // Handle 6 is an EmptyDynamicArray, mirroring FAthenaCarPlayerSlot::ExitSockets, and it is
        // here as a REGRESSION GUARD. The first version of FRepPropertyDef.IsModelled said an
        // EmptyDynamicArray counts as modelled - true of the Kind (it writes a constant, so it needs
        // no getter) and catastrophic here: every seat would have been sent an EMPTY exit-socket
        // array, destroying the very data a partial update exists to preserve. It crashed on the
        // snapshot side first, which is the only reason it was caught before a live test.
        const int emptyArrayChild = 6;

        var children = new FRepPropertyDef[handlesPerElement];
        for (var index = 0; index < handlesPerElement; index++) {
            children[index] = (index + 1) switch {
                modelledChild => new FRepPropertyDef {
                    Name = "Modelled",
                    Kind = ERepPropertyKind.Float,
                    GetFloatValue = element => ((Element) element).Value
                },
                emptyArrayChild => new FRepPropertyDef {
                    Name = "ReservedArray",
                    Kind = ERepPropertyKind.EmptyDynamicArray
                },
                _ => new FRepPropertyDef { Name = $"Reserved{index + 1}", Kind = ERepPropertyKind.Bool }
            };
        }

        failures += Check("an EmptyDynamicArray child is left alone",
            children[emptyArrayChild - 1].IsModelled, false);

        var layout = new FRepLayout(new FRepPropertyDef[] {
            new() { Name = "First", Kind = ERepPropertyKind.Bool },
            new() { Name = "Second", Kind = ERepPropertyKind.Bool },
            new() {
                Name = "Array",
                Kind = ERepPropertyKind.StructArray,
                Children = children,
                GetStructArrayValue = holder => ((Holder) holder).Elements
            }
        });

        var holder = new Holder();
        holder.Elements.Add(new Element { Value = 1.5f });
        holder.Elements.Add(new Element { Value = -2.25f });
        holder.Elements.Add(new Element { Value = 7f });

        var payload = new FNetBitWriter(4096);
        layout.WriteChangedProperties(payload, holder, new HashSet<string> { "Array" });

        var reader = new FBitReader(payload.GetData(), (int) payload.GetNumBits());

        // The leading bDoChecksum bit, then the array's own handle - 3, because the two bools ahead
        // of it take handles 1 and 2.
        failures += Check("bDoChecksum bit", reader.ReadBit(), false);
        failures += Check("array handle", ReadPacked(reader), 3u);

        // ArrayNum is a RAW uint16 - deliberately not packed, unlike every handle around it.
        failures += Check("element count", ReadRawUInt16(reader), (ushort) holder.Elements.Count);

        for (var index = 0; index < holder.Elements.Count; index++) {
            var expected = (uint) (index * handlesPerElement + modelledChild);
            failures += Check($"element {index} handle", ReadPacked(reader), expected);
            failures += Check($"element {index} value", ReadFloat(reader),
                ((Element) holder.Elements[index]).Value);
        }

        failures += Check("array terminator", ReadPacked(reader), 0u);
        failures += Check("payload terminator", ReadPacked(reader), 0u);
        failures += Check("bits consumed", reader.Pos, payload.GetNumBits());

        // The snapshot half of the same rule - this is what actually threw when IsModelled was
        // wrong, and it has to agree with the writer about which members exist.
        var shadow = new Dictionary<string, object?>();
        failures += Check("first compare reports a change",
            layout.CompareProperties(holder, new HashSet<string> { "Array" }, shadow).Count, 1);

        FRepLayout.CommitShadowState(
            layout.CompareProperties(holder, new HashSet<string> { "Array" }, shadow), shadow);
        failures += Check("an unchanged array compares equal",
            layout.CompareProperties(holder, new HashSet<string> { "Array" }, shadow).Count, 0);

        holder.Elements[1] = new Element { Value = 99f };
        failures += Check("a changed element is noticed",
            layout.CompareProperties(holder, new HashSet<string> { "Array" }, shadow).Count, 1);

        // 24, 55, 86 for a three-seat vehicle - printed because these are the numbers that appear in
        // a real push, and a reader comparing this against RepLayout.cpp should not have to re-derive
        // them from the assertions above.
        Console.WriteLine($"StructArraySelfTest: {handlesPerElement} handles per element, member " +
                          $"{modelledChild} of each - handles " +
                          $"{string.Join(", ", Enumerable.Range(0, holder.Elements.Count).Select(i => i * handlesPerElement + modelledChild))} " +
                          $"in {payload.GetNumBits()} bits. " +
                          (failures == 0 ? "OK." : $"{failures} FAILURE(S)."));

        return failures == 0;
    }

    private static int Check<T>(string what, T got, T expected) {
        if (Equals(got, expected)) return 0;

        Console.WriteLine($"StructArraySelfTest: {what} - got {got}, expected {expected}");
        return 1;
    }

    private static unsafe uint ReadPacked(FBitReader reader) {
        uint value = 0;
        reader.SerializeIntPacked(&value);
        return value;
    }

    private static unsafe ushort ReadRawUInt16(FBitReader reader) {
        ushort value = 0;
        reader.SerializeBits(&value, 16);
        return value;
    }

    private static unsafe float ReadFloat(FBitReader reader) {
        uint bits = 0;
        reader.SerializeBits(&bits, 32);
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
