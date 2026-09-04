using System;
using System.Collections;
using System.Linq;
using System.Reflection;

// Prints the wire handle FRepLayout ACTUALLY assigns to every property of a layout, read out of the
// constructed layout's own private _cmds list.
//
// WHY THIS EXISTS, AND WHY Tools/RepHandles/verify_cs_handles.py IS NOT ENOUGH. That script reads
// NativeRepLayouts.cs as TEXT and counts entries in a named table. Two things defeat it:
//
//   1. A table entry is not a handle. AActor's AttachmentReplication is ONE StructRecurse entry that
//      flattens into SIX handles (7-12). The Python script knows this rule - but only for whole
//      tables it can name.
//   2. A table built from a PREFIX of another table (`HandlePrefix(BuildingActorProps, 37)`, which
//      the supply llama needs because it branches off ABuildingActor rather than extending it) has
//      no textual form the script can follow at all. It would read the whole parent table.
//
// That blind spot cost a live disconnect: the llama's table was cut with `Take(37)`, which takes 37
// ENTRIES = 42 HANDLES, leaving five of ABuildingSMActor's properties in place and pushing Looted
// from 39 to 44. The client named the number exactly -
// "ReceiveProperties: Invalid property terminator handle - Handle=44" - and then closed the
// connection. Asking the constructed layout, rather than parsing the source that builds it, cannot
// have that class of blind spot.
//
//     dotnet run --project Tools/HandleDump                 every layout, every handle
//     dotnet run --project Tools/HandleDump <substring>     only layouts whose name matches
//
// Cross-check a result against the SDK derivation with:
//     python Tools/RepHandles/rep_handles.py <UEClassName>

// COVERAGE MODE:
//     dotnet run --project Tools/HandleDump -- --coverage            every layout, one line each
//     dotnet run --project Tools/HandleDump -- --coverage <substring>
//
// Answers "how much of each layout do we actually SEND", which is section D of
// PriveDev/dumpwork/GAP-vs-PR30.md. That section's numbers were counted by hand and have gone stale
// twice, because a Reserved() entry is INDISTINGUISHABLE FROM A REAL ONE IN THE SOURCE TEXT: several
// tables are built as a list of Reserved placeholders that a later loop then overwrites in place
// (BuildPlayerAttrSetProps' `Send(...)` helper is the clearest case - the source reads as 36
// Reserved lines and the constructed layout is nothing like that). Ask the layout, as ever.
//
// The test for "implemented" is that the def carries at least one Get*Value delegate. Reserved(name)
// sets none, so a placeholder holds a handle open - keeping every handle after it correctly numbered -
// and writes nothing.
//
// StructRecurse defs are not counted: they take no handle of their own, only their children do.

var coverage = args.Contains("--coverage");
var filter = args.FirstOrDefault(a => !a.StartsWith("--"));

var asm = Assembly.Load("AFortOnlineBeacon");
var layoutType = asm.GetType("AFortOnlineBeacon.Net.Replication.FRepLayout", true)!;
var cmdsField = layoutType.GetField("_cmds", BindingFlags.NonPublic | BindingFlags.Instance)!;
var tables = asm.GetType("AFortOnlineBeacon.Net.Replication.NativeRepLayouts", true)!;
var defType = asm.GetType("AFortOnlineBeacon.Net.Replication.FRepPropertyDef", true)!;

var getters = defType
    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(p => p.Name.StartsWith("Get") && p.Name.EndsWith("Value"))
    .ToArray();

bool IsImplemented(object def) => getters.Any(g => g.GetValue(def) != null);

var fields = tables
    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .Where(f => f.FieldType == layoutType)
    .OrderBy(f => f.Name);

var totalSent = 0;
var totalHandles = 0;

foreach (var field in fields) {
    if (filter != null && !field.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

    var cmds = (IEnumerable) cmdsField.GetValue(field.GetValue(null))!;

    if (!coverage) Console.WriteLine($"===== {field.Name} =====");
    var count = 0;
    var sent = 0;

    foreach (var cmd in cmds) {
        count++;
        var def = cmd.GetType().GetProperty("Def")!.GetValue(cmd)!;
        var handle = cmd.GetType().GetProperty("RelativeHandle")!.GetValue(cmd);
        var name = (string) def.GetType().GetProperty("Name")!.GetValue(def)!;
        var kind = def.GetType().GetProperty("Kind")!.GetValue(def);
        if (IsImplemented(def)) sent++;
        if (!coverage) Console.WriteLine($"{handle,5}\t{name}\t{kind}");
    }

    totalSent += sent;
    totalHandles += count;

    if (coverage) {
        var percent = count == 0 ? 100 : 100.0 * sent / count;
        Console.WriteLine($"{field.Name,-34} {sent,4}/{count,-4} sent  {percent,5:0.0}%  ({count - sent} reserved)");
        continue;
    }

    Console.WriteLine($"      {count} handle(s), {sent} sent, {count - sent} reserved\n");
}

if (coverage) {
    var percent = totalHandles == 0 ? 100 : 100.0 * totalSent / totalHandles;
    Console.WriteLine($"\n{"TOTAL",-34} {totalSent,4}/{totalHandles,-4} sent  {percent,5:0.0}%");
}
