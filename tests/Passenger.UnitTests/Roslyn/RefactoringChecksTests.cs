using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>What C#'s refactorings refuse in the words they are asked, the one rule the extension refuses by and each
/// dialog checks its options by: a new file's name that is a path, a namespace that is no name, and a signature's
/// parameter nested too deeply to read as its declaration will be made.</summary>
public sealed class RefactoringChecksTests
{
    private static readonly Signature Scale = new(null, "method", "Calc", "public static", "Scale", "int",
        [new Parameter("", "int", "value", ""), new Parameter("", "int", "factor", "")], false);

    [Fact]
    public void A_new_files_name_a_namespace_and_a_signatures_parameters_are_refused_in_the_words_asked() => Cases.All(
        ("a new file's name is a C# file's name, never a path", () => Cases.Each<(string, string?)>(
        [
            ("IOrder.CS", null),
            ("sub/IOrder.cs", "'sub/IOrder.cs' is not a file name"),
            ("sub\\IOrder.cs", "'sub\\IOrder.cs' is not a file name"),
        ], row => Assert.Equal(row.Item2, RefactoringChecks.WhyNotFileName(row.Item1)))),
        ("a namespace is read part by part, each trimmed", () => Cases.Each<(string, string?)>(
        [
            ("Shop.Sales", null),
            (" Shop . Reports ", "'Order' is already in namespace 'Shop.Reports'"),
            ("Shop.class", "'Shop.class' is not a valid namespace name"),
        ], row => Assert.Equal(row.Item2, RefactoringChecks.WhyNotNamespace(row.Item1, "Order", "Shop.Reports")))),
        ("a parameter whose type or value reads alone but not as its declaration is refused saying it is nested too deeply", () =>
        {
            string type = string.Concat(Enumerable.Repeat("List<", 1000)) + "int" + new string('>', 1000);
            string value = new string('(', 1000) + "1" + new string(')', 1000);
            Assert.Null(WhyNot(Kept(0) with { Type = "List<int>" }, Kept(1)));
            Assert.Equal("A parameter is nested too deeply to read", WhyNot(Kept(0) with { Type = type }, Kept(1)));
            Assert.Equal("A parameter is nested too deeply to read", WhyNot(Kept(0), Kept(1) with { DefaultValue = value }));
            Assert.Equal("A parameter's type is nested too deeply to read", WhyNot(Kept(0) with { Type = "List<" + type + ">" }, Kept(1)));
            Assert.NotNull(RefactoringChecks.Declared("", "List<int>", "value", "null"));
        }));

    private static string? WhyNot(params ParameterChange[] parameters) => RefactoringChecks.WhyNotSignature(Scale, "Scale", "int", parameters);

    private static ParameterChange Kept(int from) => new(from, "", Scale.Parameters[from].Type, Scale.Parameters[from].Name, "", "");
}
