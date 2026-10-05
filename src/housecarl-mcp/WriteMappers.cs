using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

internal sealed partial class RecordWrites
{
    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for a create: the type is the create type, and a stray formid is refused rather than ignored.</summary>
    WriteRequest? MapCreateEdit(BulkOp op, int index, string recordType, out string? error)
    {
        error = null;
        // ops[i] is the create surface's own member word, so a refusal names the handle the caller can act on.
        var where = $"ops[{index}]";
        if (!string.IsNullOrWhiteSpace(op.Formid))
        {
            error = $"{where}: a create operation sets a field on the NEW record, so it takes no formid (the new record's id is auto-allocated). Remove formid='{op.Formid}'.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where}: field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where}: field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        if (string.Equals(op.Verb, WriteVerbs.Transplanting, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // Named as the create surface spells it, which declares no from_plugin member of its own.
            error = $"{where}: op=\"CopyFrom\" copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
            return null;
        }
        if (RefuseStrayBesideCompose(op, where, out error)) return null;
        var verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb;
        if (RefuseUnreadKey(op, verb, where, out error)) return null;

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
        };
    }

    /// <summary>Map a wire op to a core <see cref="WritePatchBuilder.PatchEdit"/>; RecordType is left to the engine, which derives it from the resolved winner.</summary>
    WritePatchBuilder.PatchEdit? MapEdit(FormIdDoor door, BulkOp op, int index, out string? error,
                                         string? fromRecord = null, string? origin = null)
    {
        error = null;
        // The caller's own spelling for this edit: ops[i] inline, or the pair and path a zip-generated op came from.
        var where = origin ?? $"ops[{index}]";
        if (string.IsNullOrWhiteSpace(op.Formid)) { error = $"{where}: formid is required."; return null; }
        FormKey fk;
        try { fk = door.Parse(op.Formid); }
        catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where}: ", $"{where}: bad formid '{op.Formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where} ({op.Formid}): field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where} ({op.Formid}): field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        var verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb;

        // The cross-record copy source: a named source record makes from_source optional, and a source equal to the target is refused.
        FormKey? fromKey = null;
        if (!string.IsNullOrWhiteSpace(fromRecord))
        {
            try { fromKey = door.Parse(fromRecord); }
            catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where} ({op.Formid}): ", $"{where} ({op.Formid}): bad from '{fromRecord}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
            if (fromKey == fk)
            { error = $"{where} ({op.Formid}): from names the SAME record as formid — copying a record's field onto itself is a no-op. Drop from=, and name the plugin whose version to copy in from_source=."; return null; }
        }

        var fromPlugin = MapFromPlugin(op, verb, $"{where} ({op.Formid})", spec, specs, fromKey is not null, out error);
        if (error is not null) return null;
        if (RefuseStrayBesideCompose(op, where, out error)) return null;
        if (RefuseUnreadKey(op, verb, where, out error)) return null;

        return new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = path, Verb = verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
            FromPlugin = fromPlugin, FromTarget = fromKey,
        };
    }

    /// <summary>Validate and extract from_plugin for a CopyFrom op: required with, and only with, CopyFrom, which takes no authored value. Null otherwise.</summary>
    static string? MapFromPlugin(BulkOp op, string verb, string where, StructSpec? spec, IReadOnlyList<StructSpec>? specs,
        bool hasSourceRecord, out string? error)
    {
        error = null;
        if (!string.Equals(verb, "CopyFrom", StringComparison.Ordinal))   // match the engine's ordinal verb compare, so a mis-cased verb fails the same way everywhere
        {
            if (!string.IsNullOrWhiteSpace(op.FromPlugin))
                error = $"{where}: from_source is only valid with op=CopyFrom (got op={verb}).";
            return null;
        }
        // The "a copy carries no authored value" rule is independent of the pole, so it is checked FIRST.
        if (op.Value is not null || op.Values is not null || op.Entries is not null || spec is not null || specs is not null)
        {
            error = $"{where}: CopyFrom copies the field from the source record's version — it takes no value/values/entries/compose/composes.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // A named source record identifies what to copy on its own, so the pole defaults to that record's winner.
            if (hasSourceRecord) return null;
            error = $"{where}: CopyFrom requires from_source — the plugin whose version of this record to copy field_path from.";
            return null;
        }
        return op.FromPlugin.Trim();
    }

    /// <summary>Build a core composition <see cref="StructSpec"/> from the wire shape: flat <c>fields</c>, positional
    /// <c>ctor_args</c>, and nested <c>sets</c>, each of which may itself compose a sub-arm. A malformed spec is a named error.</summary>
    internal static StructSpec? MapStruct(StructInput s, string where, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s.Type)) { error = $"{where}: compose.type is required (the arm / element type, e.g. 'LeveledItemEntry')."; return null; }
        List<WriteRequest>? sets = null;
        if (s.Sets is { Length: > 0 })
        {
            sets = new List<WriteRequest>(s.Sets.Length);
            foreach (var ns in s.Sets)
            {
                if (string.IsNullOrWhiteSpace(ns.Path)) { error = $"{where}: each compose.sets[] needs a path."; return null; }
                StructSpec? nestedSpec = null;
                if (ns.Compose is not null)
                {
                    nestedSpec = MapStruct(ns.Compose, where, out error);
                    if (error is not null) return null;
                }
                sets.Add(new WriteRequest
                {
                    RecordType = s.Type!, Path = SplitPath(ns.Path),
                    Verb = string.IsNullOrWhiteSpace(ns.Verb) ? "Set" : ns.Verb, Key = ns.Key, Value = ns.Value,
                    Struct = nestedSpec,
                });
            }
        }
        return new StructSpec { Type = s.Type!, Fields = s.Fields, CtorArgs = s.CtorArgs, Sets = sets };
    }

    /// <summary>compose= and composes= build their elements themselves, so a value, values or entries beside either would be dropped unwritten; refuse it. Empty values/entries count as absent.</summary>
    static bool RefuseStrayBesideCompose(BulkOp op, string where, out string? error)
    {
        error = null;
        if (op.Compose is null && op.Composes is null) return false;
        var stray = new List<string>();
        if (op.Value is not null) stray.Add("value=");
        if (op.Values is { Length: > 0 }) stray.Add("values=");
        if (op.Entries is { Count: > 0 }) stray.Add("entries=");
        if (stray.Count == 0) return false;
        var name = op.Composes is not null ? "composes=" : "compose=";
        var builds = op.Composes is not null ? "each element" : "the element";
        error = $"{where}: {name} builds {builds} itself, so it takes no {string.Join(" or ", stray)} beside it — remove {string.Join(" and ", stray)}, or drop {name}.";
        return true;
    }

    /// <summary>Refuse key= on a verb that never reads it whatever the field: ReplaceAll, Merge, CopyFrom, and Add with
    /// composes=, which appends. A list Add and a whole-field Remove depend on the field's shape, so the rulebook refuses those.</summary>
    static bool RefuseUnreadKey(BulkOp op, string verb, string where, out string? error)
    {
        error = null;
        if (op.Key is null) return false;
        var reason = verb switch
        {
            "ReplaceAll" => "ReplaceAll replaces the whole field",
            "Merge" => "Merge merges the pairs in entries=",
            WriteVerbs.Transplanting => "CopyFrom copies the whole field",
            "Add" when op.Composes is not null => "composes= with Add appends each element at the end of the list",
            _ => null,
        };
        if (reason is null) return false;
        var remedy = verb == "Add" ? "remove key=, or place one element at an index with compose= and InsertAtIndex" : "remove key=";
        error = $"{where}: {reason}, so it takes no key= — {remedy}.";
        return true;
    }

    /// <summary>Map a wire op's composes[] to core StructSpecs through the same <see cref="MapStruct"/> the singular compose uses; mutually exclusive with it.</summary>
    static List<StructSpec>? MapComposes(BulkOp op, string where, StructSpec? singular, out string? error)
    {
        error = null;
        if (op.Composes is null) return null;
        if (singular is not null)
        {
            error = $"{where}: pass compose= (one element) OR composes= (many), not both.";
            return null;
        }
        if (op.Composes.Length == 0)
        {
            // An empty composes=[] is the clear intent for a ReplaceAll; for any other verb it is a caller mistake.
            if (!string.Equals(op.Verb, "ReplaceAll", StringComparison.Ordinal))
            {
                error = $"{where}: composes=[] is empty — supply one or more element specs (or compose= for one); an empty composes= is only meaningful with op=ReplaceAll, to CLEAR the list.";
                return null;
            }
            return new List<StructSpec>();   // ReplaceAll composes=[] clears the modeled list
        }
        var specs = new List<StructSpec>(op.Composes.Length);
        for (int j = 0; j < op.Composes.Length; j++)
        {
            var s = MapStruct(op.Composes[j], $"{where} composes[{j}]", out error);
            if (error is not null) return null;
            specs.Add(s!);
        }
        return specs;
    }

    static string[] SplitPath(string dotted)
        => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
