using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;

namespace Passenger.Debugger;

/// <summary>The well-known collections and records shown from what they store while no code of the program may run (a
/// snapshot's capture, implicit evaluation off): a collection's count and items, a record's members as its printing names
/// them, each read from fields of the names and kinds the runtime's own types declare. A type whose fields are not so
/// (another runtime's shape, a record that prints otherwise) is shown as any other object, never guessed at.</summary>
public partial class ManagedDebugger
{
    /// <summary>The most items a collection read from its fields lists of itself; the rest are counted.</summary>
    private const int StoredItemsListed = 100;

    /// <summary>The modules the runtime's collections are compiled into.</summary>
    private static readonly string[] CollectionModules = ["System.Private.CoreLib.dll", "System.Collections.dll"];

    /// <summary>Whether a value may be read only from what it stores: implicit evaluation is off, and the read is not one the
    /// person asked to run.</summary>
    private bool CodeMayNotRun => EvalStatus.ImplicitOff is not null && !EvalStatus.Explicit;

    private enum StoredKind
    {
        List,
        Dictionary,
        HashSet,
        Queue,
        Stack,
    }

    /// <summary>A well-known collection as it stores its items: its kind, its count, the array holding them, and where they
    /// begin (a queue's head) or how many slots to look through (a dictionary's or a set's entries, some free).</summary>
    private sealed record StoredCollection(StoredKind Kind, int Count, ICorDebugArrayValue? Items, int Head, int Slots);

    /// <summary>What a well-known collection of the runtime stores, or null for any other value, or one whose fields are not
    /// those it is known by.</summary>
    private StoredCollection? StoredOf(ICorDebugObjectValue value)
    {
        try
        {
            ICorDebugType type = value.ExactType;
            if (FindModule(type.Class.Module) is not { } module || !CollectionModules.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }
            string name = GetCorDebugTypeFriendlyName(type);
            int generic = name.IndexOf('<', StringComparison.Ordinal);
            StoredKind? kind = (generic < 0 ? name : name[..generic]) switch
            {
                "System.Collections.Generic.List" => StoredKind.List,
                "System.Collections.Generic.Dictionary" => StoredKind.Dictionary,
                "System.Collections.Generic.HashSet" => StoredKind.HashSet,
                "System.Collections.Generic.Queue" => StoredKind.Queue,
                "System.Collections.Generic.Stack" => StoredKind.Stack,
                _ => null,
            };
            return kind switch
            {
                StoredKind.List => Contiguous(StoredKind.List, StoredArray(value, "_items"), StoredInt(value, "_size"), 0),
                StoredKind.Queue => Contiguous(StoredKind.Queue, StoredArray(value, "_array"), StoredInt(value, "_size"), StoredInt(value, "_head")),
                StoredKind.Stack => Contiguous(StoredKind.Stack, StoredArray(value, "_array"), StoredInt(value, "_size"), 0),
                StoredKind.Dictionary or StoredKind.HashSet => Entries(kind.Value, value),
                _ => null,
            };
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"A collection's fields could not be read: {error.Message}");
            return null;
        }

        static StoredCollection? Contiguous(StoredKind kind, (bool Found, ICorDebugArrayValue? Array) items, int? size, int? head) =>
            items.Found && size is int count and >= 0 && head is int first and >= 0
            && (items.Array is null ? count == 0 : count <= Length(items.Array) && (first < Length(items.Array) || count == 0))
                ? new StoredCollection(kind, count, items.Array, first, count)
                : null;

        StoredCollection? Entries(StoredKind kind, ICorDebugObjectValue set) =>
            StoredArray(set, "_entries") is (true, var entries) && StoredInt(set, "_count") is int slots and >= 0 && StoredInt(set, "_freeCount") is int free
            && free >= 0 && free <= slots && (entries is null ? slots == 0 : slots <= Length(entries))
                ? new StoredCollection(kind, slots - free, entries, 0, slots)
                : null;
    }

    /// <summary>What a well-known collection shows as its value: its count, as its own display says it.</summary>
    private static string CountShown(StoredCollection stored) => $"Count = {Core.Numbers.Said(stored.Count)}";

    /// <summary>A collection's items, read from where it stores them, in the order it gives them (a stack's top first, a
    /// queue's head first), each opening as any value does; a list's by its index from <paramref name="expression"/>, past a
    /// hundred in ranges as an array's, the others' first hundred and then how many more there are.</summary>
    private async Task AddStoredItems(StoredCollection stored, ThreadId threadId, FrameStackDepth stackDepth, string? expression, List<VariableInfo> result)
    {
        if (stored.Items is not { } items || stored.Count == 0)
        {
            return;
        }
        if (stored.Kind is StoredKind.List)
        {
            await AddListElements(items, stored.Count, threadId, stackDepth, expression, result);
            return;
        }
        int listed = 0;
        int length = Length(items);
        for (int slot = 0; slot < stored.Slots && listed < StoredItemsListed; slot++)
        {
            int at = stored.Kind switch
            {
                StoredKind.Queue => (stored.Head + slot) % length,
                StoredKind.Stack => stored.Count - 1 - slot,
                _ => slot,
            };
            ICorDebugValue element = items.GetElement(1, [checked((uint)at)]);
            if (stored.Kind is StoredKind.Dictionary or StoredKind.HashSet)
            {
                if (element.UnwrapDebugValue() is not ICorDebugObjectValue entry
                    || StoredInt(entry, stored.Kind is StoredKind.Dictionary ? "next" : "Next") is not int next || next < -1)
                {
                    continue;
                }
                if (stored.Kind is StoredKind.Dictionary)
                {
                    await AddEntryRow(entry, threadId, stackDepth, result);
                    listed++;
                    continue;
                }
                element = StoredField(entry, "Value") ?? throw new InvalidOperationException("A set's entry holds no Value");
            }
            string name = $"[{listed}]";
            await WithFailureHandling(result, name, async () => result.Add(await ValueRow(name, null, element, threadId, stackDepth, displayed: true, namesPointee: false)));
            listed++;
        }
        if (stored.Count > listed)
        {
            result.Add(new VariableInfo
            {
                Name = "…",
                Value = $"{Core.Numbers.Said(stored.Count - listed)} more, not listed while the program's code may not run",
                Type = null,
                VariablesReference = 0,
                PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual },
            });
        }
    }

    /// <summary>A list's first <paramref name="count"/> elements of the array it keeps them in, past a hundred in ranges.</summary>
    private async Task AddListElements(ICorDebugArrayValue items, int count, ThreadId threadId, FrameStackDepth stackDepth, string? expression, List<VariableInfo> result)
    {
        if (count <= StoredItemsListed)
        {
            await AddArrayElements(items, threadId, stackDepth, result, count: checked((uint)count), expression: expression);
            return;
        }
        for (int start = 0; start < count; start += StoredItemsListed)
        {
            uint taken = (uint)Math.Min(StoredItemsListed, count - start);
            result.Add(GroupRow($"[{start}..{start + taken - 1}]", new VariablesReference(StoredReferenceKind.ArrayRange, items, threadId, stackDepth, null, [], (uint)start, taken,
                Expression: expression), PresentationHintKind.Virtual));
        }
    }

    /// <summary>A dictionary's entry as its debugger view shows one: named by its key, its value the row's.</summary>
    private async Task AddEntryRow(ICorDebugObjectValue entry, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        ICorDebugValue key = StoredField(entry, "key") ?? throw new InvalidOperationException("A dictionary's entry holds no key");
        ICorDebugValue value = StoredField(entry, "value") ?? throw new InvalidOperationException("A dictionary's entry holds no value");
        string named = $"[{(await GetDisplayedValueAsync(key, threadId, stackDepth, true)).value}]";
        await WithFailureHandling(result, named, async () => result.Add(await ValueRow(named, null, value, threadId, stackDepth, displayed: false, namesPointee: false)));
    }

    /// <summary>A record's members as its printing names them, "Order { Id = 1, Name = "Tea" }", each value as the debugger
    /// shows it, its base record's first; null for a value that is no record, one whose printing or ToString is its own, or
    /// one with a public member whose value only its code gives (a property with a body).</summary>
    private async Task<string?> RecordShownAsync(ICorDebugObjectValue value, ThreadId threadId, FrameStackDepth stackDepth, int nesting)
    {
        List<ICorDebugType> chain = [];
        for (ICorDebugType? type = value.ExactType; type is not null; type = type.Base)
        {
            IMetaDataImport metadata = type.Class.Module.GetMetaDataInterface<IMetaDataImport>();
            if (metadata.GetTypeDefProps(type.Class.Token).szTypeDef is "System.Object" or "System.ValueType")
            {
                break;
            }
            if (!Generated(metadata, type.Class.Token, "PrintMembers") || !Generated(metadata, type.Class.Token, "ToString"))
            {
                return null;
            }
            chain.Insert(0, type);
        }
        if (chain.Count == 0)
        {
            return null;
        }
        List<string> members = [];
        foreach (ICorDebugType type in chain)
        {
            ICorDebugClass declaring = type.Class;
            IMetaDataImport metadata = declaring.Module.GetMetaDataInterface<IMetaDataImport>();
            List<(int Order, string Name, mdFieldDef Field)> printed = [];
            foreach (mdFieldDef field in metadata.EnumFields(declaring.Token))
            {
                string name = metadata.GetFieldProps(field).szField;
                if (field.IsPublic(metadata) && !field.IsStatic(metadata) && !Extensions.IsCompilerGeneratedFieldName(name))
                {
                    printed.Add(((int)field.Rid, name, field));
                }
            }
            foreach (mdProperty property in metadata.EnumProperties(declaring.Token))
            {
                (string name, _, _, nint signature, int length, _, _, _, _, mdMethodDef getter, _, _) = metadata.GetPropertyProps(property);
                if (getter.IsNil || !property.IsPublic(metadata) || property.IsStatic(metadata) || name is "EqualityContract" || TakesParameters(signature, length))
                {
                    continue;
                }
                mdFieldDef backing = metadata.EnumFieldsWithName(declaring.Token, $"<{name}>k__BackingField").FirstOrDefault();
                if (backing.IsNil || !IsCompilerGenerated(metadata, getter))
                {
                    return null;
                }
                printed.Add(((int)backing.Rid, name, backing));
            }
            foreach ((_, string name, mdFieldDef field) in printed.OrderBy(p => p.Order))
            {
                ICorDebugValue member = value.GetFieldValue(declaring, field);
                FormattedValue shown = await FormatCorDebugValueAsync(ResolveCorDebugValue(member), threadId, stackDepth, escapeStringValue: true, nesting + 1);
                members.Add($"{name} = {shown.Value}");
            }
        }
        string typeName = GetCorDebugTypeFriendlyName(value.ExactType);
        int generic = typeName.IndexOf('<', StringComparison.Ordinal);
        string bare = generic < 0 ? typeName : typeName[..generic];
        string shortName = bare[(bare.LastIndexOf('.') + 1)..];
        return members.Count == 0 ? $"{shortName} {{ }}" : $"{shortName} {{ {string.Join(", ", members)} }}";

        static bool Generated(IMetaDataImport metadata, mdTypeDef type, string method) =>
            metadata.EnumMethodsWithName(type, method).ToArray() is [var only] && IsCompilerGenerated(metadata, only);
    }

    /// <summary>A field of a value's own type by its name, or null where its type has none so named.</summary>
    private static ICorDebugValue? StoredField(ICorDebugObjectValue value, string name)
    {
        ICorDebugClass declaring = value.ExactType.Class;
        mdFieldDef field = declaring.Module.GetMetaDataInterface<IMetaDataImport>().EnumFieldsWithName(declaring.Token, name).FirstOrDefault();
        return field.IsNil ? null : value.GetFieldValue(declaring, field);
    }

    /// <summary>A field of a value's own type holding an int, or null where it has none so named or of another kind.</summary>
    private static int? StoredInt(ICorDebugObjectValue value, string name) =>
        StoredField(value, name)?.UnwrapDebugValue() is ICorDebugGenericValue { Type: CorElementType.I4 } stored ? BitConverter.ToInt32(stored.GetValueAsBytes()) : null;

    /// <summary>A field of a value's own type holding a one-dimensional array: found, and the array (null where it holds
    /// none); not found where there is no such field or it holds anything else.</summary>
    private static (bool Found, ICorDebugArrayValue? Array) StoredArray(ICorDebugObjectValue value, string name) => StoredField(value, name) switch
    {
        null => (false, null),
        ICorDebugReferenceValue { IsNull: true } => (true, null),
        { } held => held.UnwrapDebugValue() is ICorDebugArrayValue { Rank: 1 } array ? (true, array) : (false, null),
    };

    private static int Length(ICorDebugArrayValue array) => checked((int)array.GetDimensions(1)[0]);
}
