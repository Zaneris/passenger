using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>What C#'s refactorings refuse in the words they are asked, the one rule the extension refuses by and each
/// dialog checks its options by: a new file's name, a namespace, and a signature's parameters, each read as its row is
/// and then whole, as its declaration will be made.</summary>
public sealed class RefactoringChecksTests
{
    private static readonly Signature Scale = new(null, "method", "Calc", "public static", "Scale", "int",
        [new Parameter("", "int", "value", ""), new Parameter("", "int", "factor", "")], false);

    [Theory]
    [InlineData("IOrder.cs", null)]
    [InlineData("IOrder.CS", null)]
    [InlineData("", "file name is empty")]
    [InlineData("IOrder.txt", "The new file name extension is not valid for source code files")]
    [InlineData("sub/IOrder.cs", "'sub/IOrder.cs' is not a file name")]
    [InlineData("sub\\IOrder.cs", "'sub\\IOrder.cs' is not a file name")]
    public void A_new_files_name_is_a_C_sharp_files_name_never_a_path(string name, string? why) =>
        Assert.Equal(why, RefactoringChecks.WhyNotFileName(name));

    [Theory]
    [InlineData("Sales", null)]
    [InlineData("Shop.Sales", null)]
    [InlineData(" Shop . Reports ", "'Order' is already in namespace 'Shop.Reports'")]
    [InlineData("Shop.Reports", "'Order' is already in namespace 'Shop.Reports'")]
    [InlineData(" ", "Specify namespace")]
    [InlineData("Shop.class", "'Shop.class' is not a valid namespace name")]
    [InlineData("Shop..Sales", "'Shop..Sales' is not a valid namespace name")]
    public void A_namespace_is_read_part_by_part_each_trimmed(string written, string? why) =>
        Assert.Equal(why, RefactoringChecks.WhyNotNamespace(written, "Order", "Shop.Reports"));

    [Fact]
    public void A_parameter_is_read_as_its_row_reads_its_type_before_its_name() =>
        Assert.Equal("A parameter has an invalid type", WhyNot(Kept(0), Kept(1), New("int[", "2nd")));

    [Fact]
    public void A_parameter_whose_type_or_value_reads_alone_but_not_as_its_declaration_is_refused_saying_it_is_nested_too_deeply()
    {
        string type = string.Concat(Enumerable.Repeat("List<", 1000)) + "int" + new string('>', 1000);
        string value = new string('(', 1000) + "1" + new string(')', 1000);
        Assert.Null(WhyNot(Kept(0) with { Type = "List<int>" }, Kept(1)));
        Assert.Equal("A parameter is nested too deeply to read", WhyNot(Kept(0) with { Type = type }, Kept(1)));
        Assert.Equal("A parameter is nested too deeply to read", WhyNot(Kept(0), Kept(1) with { DefaultValue = value }));
        Assert.Equal("A parameter's type is nested too deeply to read", WhyNot(Kept(0) with { Type = "List<" + type + ">" }, Kept(1)));
        Assert.NotNull(RefactoringChecks.Declared("", "List<int>", "value", "null"));
    }

    [Fact]
    public void A_parameters_values_modifier_and_place_are_checked_as_the_declaration_needs()
    {
        Assert.Equal("Error in default value expression", WhyNot(Kept(0), Kept(1) with { DefaultValue = "1 +" }));
        Assert.Equal("Error in default value expression", WhyNot(Kept(0), Kept(1), New("int", "offset") with { CallValue = "1 +" }));
        Assert.Equal("'value' parameter must appear after all other parameters", WhyNot(Kept(0) with { Modifier = "params" }, Kept(1)));
        Assert.Equal("Optional parameters must appear after all regular parameters", WhyNot(Kept(0) with { DefaultValue = "1" }, Kept(1)));
        Assert.Equal("'readonly' is not a parameter modifier", WhyNot(Kept(0) with { Modifier = "readonly" }, Kept(1)));
        Assert.Equal("A parameter is listed twice", WhyNot(Kept(0), Kept(0) with { Name = "again" }));
        Assert.Equal("Parameters should have different names", WhyNot(Kept(0), Kept(1) with { Name = "value" }));
        Assert.Null(WhyNot(Kept(1), Kept(0), New("int", "offset") with { CallValue = "0" }));
    }

    private static string? WhyNot(params ParameterChange[] parameters) => RefactoringChecks.WhyNotSignature(Scale, "Scale", "int", parameters);

    private static ParameterChange Kept(int from) => new(from, "", Scale.Parameters[from].Type, Scale.Parameters[from].Name, "", "");

    private static ParameterChange New(string type, string name) => new(-1, "", type, name, "", "");
}
