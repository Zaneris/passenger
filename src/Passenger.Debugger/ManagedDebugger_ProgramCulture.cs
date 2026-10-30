using System.Globalization;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The program's culture read from its memory, none of its code run, for the numbers the debugger writes where the
/// program can run none: the culture CultureInfo.CurrentCulture gives the thread (its own, else the default for threads,
/// else the user's), its numbers' separators, signs, symbols, digits and patterns as the program holds them, else as a
/// culture of that name has them.</summary>
public partial class ManagedDebugger
{
    /// <summary>The static fields CultureInfo.CurrentCulture reads the culture from, in its order.</summary>
    private static readonly string[] CurrentCultureFields = ["s_currentThreadCulture", "s_DefaultThreadCurrentCulture", "s_userDefaultCulture"];

    /// <summary>NumberFormatInfo's fields, each by the property that sets it.</summary>
    private static readonly (string Field, Action<NumberFormatInfo, string> Set)[] NumberTexts =
    [
        ("_positiveSign", (numbers, text) => numbers.PositiveSign = text),
        ("_negativeSign", (numbers, text) => numbers.NegativeSign = text),
        ("_numberDecimalSeparator", (numbers, text) => numbers.NumberDecimalSeparator = text),
        ("_numberGroupSeparator", (numbers, text) => numbers.NumberGroupSeparator = text),
        ("_currencyDecimalSeparator", (numbers, text) => numbers.CurrencyDecimalSeparator = text),
        ("_currencyGroupSeparator", (numbers, text) => numbers.CurrencyGroupSeparator = text),
        ("_currencySymbol", (numbers, text) => numbers.CurrencySymbol = text),
        ("_nanSymbol", (numbers, text) => numbers.NaNSymbol = text),
        ("_positiveInfinitySymbol", (numbers, text) => numbers.PositiveInfinitySymbol = text),
        ("_negativeInfinitySymbol", (numbers, text) => numbers.NegativeInfinitySymbol = text),
        ("_percentDecimalSeparator", (numbers, text) => numbers.PercentDecimalSeparator = text),
        ("_percentGroupSeparator", (numbers, text) => numbers.PercentGroupSeparator = text),
        ("_percentSymbol", (numbers, text) => numbers.PercentSymbol = text),
        ("_perMilleSymbol", (numbers, text) => numbers.PerMilleSymbol = text),
    ];

    private static readonly (string Field, Action<NumberFormatInfo, int> Set)[] NumberCounts =
    [
        ("_numberDecimalDigits", (numbers, count) => numbers.NumberDecimalDigits = count),
        ("_currencyDecimalDigits", (numbers, count) => numbers.CurrencyDecimalDigits = count),
        ("_percentDecimalDigits", (numbers, count) => numbers.PercentDecimalDigits = count),
        ("_numberNegativePattern", (numbers, pattern) => numbers.NumberNegativePattern = pattern),
        ("_currencyPositivePattern", (numbers, pattern) => numbers.CurrencyPositivePattern = pattern),
        ("_currencyNegativePattern", (numbers, pattern) => numbers.CurrencyNegativePattern = pattern),
        ("_percentPositivePattern", (numbers, pattern) => numbers.PercentPositivePattern = pattern),
        ("_percentNegativePattern", (numbers, pattern) => numbers.PercentNegativePattern = pattern),
    ];

    private static readonly (string Field, Action<NumberFormatInfo, int[]> Set)[] NumberGroups =
    [
        ("_numberGroupSizes", (numbers, sizes) => numbers.NumberGroupSizes = sizes),
        ("_currencyGroupSizes", (numbers, sizes) => numbers.CurrencyGroupSizes = sizes),
        ("_percentGroupSizes", (numbers, sizes) => numbers.PercentGroupSizes = sizes),
    ];

    /// <summary>How the program's culture writes numbers on a thread, read in the frame given; null where no culture is set
    /// for it yet (the user's is made as the program first asks for it) or one cannot be read.</summary>
    internal NumberFormatInfo? ProgramNumberFormat(ThreadId threadId, FrameStackDepth stackDepth)
    {
        try
        {
            if (_modules.Values.FirstOrDefault(module => module.ModuleName == "System.Private.CoreLib.dll") is not { } coreLib
                || coreLib.Module.GetMetaDataInterface<IMetaDataImport>().FindTypeDefByNameOrNull("System.Globalization.CultureInfo", mdToken.Nil) is not { } cultureType)
            {
                return null;
            }
            ICorDebugClass cultureClass = coreLib.Module.GetClassFromToken(cultureType);
            ICorDebugFrame frame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
            ICorDebugObjectValue? culture = CurrentCultureFields
                .Select(field => FieldToken(cultureClass, field) is { } token && cultureClass.TryGetStaticFieldValue(token, frame, out ICorDebugValue? value) is Cor.S_OK
                    ? value.UnwrapDebugValueToObjectOrNull()
                    : null)
                .FirstOrDefault(found => found is not null);
            if (culture is null || FieldOf(culture, "_name") is not ICorDebugStringValue { String: { } name })
            {
                return null;
            }
            // The user's overrides (Windows' regional settings) are this user's, the debugger's too.
            bool overridden = FieldOf(culture, "_cultureData") is ICorDebugObjectValue data && FieldOf(data, "_bUseOverrides") is ICorDebugGenericValue flag
                && flag.GetValueAsBytes() is [not 0, ..];
            NumberFormatInfo numbers = (NumberFormatInfo)new CultureInfo(name, overridden).NumberFormat.Clone();
            if (FieldOf(culture, "_numInfo") is ICorDebugObjectValue held and not ICorDebugStringValue)
            {
                Copy(held, numbers);
            }
            return NumberFormatInfo.ReadOnly(numbers);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            Log($"The program's culture could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>A line the Debug Console shows.</summary>
    internal void Say(string line) => OnConsole?.Invoke($"{line}\n");

    /// <summary>The program's NumberFormatInfo's values in place of the culture's: each field read as the type its property
    /// sets, one not read (a runtime naming it otherwise) left as the culture has it.</summary>
    private static void Copy(ICorDebugObjectValue held, NumberFormatInfo numbers)
    {
        foreach ((string field, Action<NumberFormatInfo, string> set) in NumberTexts)
        {
            if (FieldOf(held, field) is ICorDebugStringValue { String: { } text })
            {
                set(numbers, text);
            }
        }
        foreach ((string field, Action<NumberFormatInfo, int> set) in NumberCounts)
        {
            if (FieldOf(held, field) is ICorDebugGenericValue { Size: 4 } count)
            {
                set(numbers, BitConverter.ToInt32(count.GetValueAsBytes()));
            }
        }
        foreach ((string field, Action<NumberFormatInfo, int[]> set) in NumberGroups)
        {
            if (FieldOf(held, field) is ICorDebugArrayValue sizes)
            {
                set(numbers, [.. Enumerable.Range(0, sizes.Count).Select(at => BitConverter.ToInt32(((ICorDebugGenericValue)sizes.GetElementAtPosition(at)).GetValueAsBytes()))]);
            }
        }
    }

    /// <summary>An object's instance field by its name, the value it refers to in place of the reference; null where its
    /// class declares none of that name.</summary>
    private static ICorDebugValue? FieldOf(ICorDebugObjectValue value, string name) =>
        FieldToken(value.Class, name) is { } token ? value.GetFieldValue(value.Class, token).UnwrapDebugValue() : null;

    /// <summary>The field a class declares by the name given; null where it declares none.</summary>
    private static mdFieldDef? FieldToken(ICorDebugClass declaring, string name)
    {
        IMetaDataImport metadata = declaring.Module.GetMetaDataInterface<IMetaDataImport>();
        foreach (mdFieldDef field in metadata.EnumFields(declaring.Token))
        {
            if (metadata.GetFieldProps(field).szField == name)
            {
                return field;
            }
        }
        return null;
    }
}
