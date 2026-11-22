using System.Globalization;
using System.Reflection;
using Passenger.Workspace.Gherkin;
using Reqnroll;
using Reqnroll.Bindings;
using Reqnroll.Bindings.CucumberExpressions;
using Reqnroll.Bindings.Reflection;
using Reqnroll.Infrastructure;
using PassengerContext = Passenger.Workspace.Gherkin.StepContext;
using ReqnrollContext = Reqnroll.Bindings.StepContext;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step bound to its definitions as Reqnroll 3.3.4's own matcher binds it, held to that matcher run here
/// in-process: one set of definitions (Cucumber Expressions and regular expressions, their parameters converted, an enum and
/// a transformation named in an expression, scopes by tag and feature, definitions alike and alike but for a parameter's
/// type), each made by Reqnroll's own binding factory and read by Passenger as from its code, and random steps of every kind
/// in random contexts, each bound by both: what binds it, whether several do alike, and whether one binds it only to fail
/// counting its parameters or converting an argument.</summary>
public sealed class StepMatcherReqnrollTests
{
    private const int Seed = 2095;
    private const int Drawn = 3000;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Each step's text, its slots filled at random, and the kind it is mostly written as.</summary>
    private static readonly (string Text, StepDefinitionType Kind)[] Steps =
    [
        ("the first number is {n}", StepDefinitionType.Given), ("I enter {s} in the {w} field", StepDefinitionType.When),
        ("the total is {f}", StepDefinitionType.Then), ("I have {n} cucumbers", StepDefinitionType.Given),
        ("I have {n} cucumber{pl} in my {belly}", StepDefinitionType.Given), ("the colour is {c}", StepDefinitionType.Given),
        ("a price of {m}", StepDefinitionType.Given), ("a cost of {m}", StepDefinitionType.Given), ("I log in", StepDefinitionType.When),
        ("it repeats", StepDefinitionType.Then), ("the count is {n}", StepDefinitionType.Given), ("the size is {n}", StepDefinitionType.Given),
        ("the calculator is cleared", StepDefinitionType.Then), ("I wait {n} seconds", StepDefinitionType.When),
        ("I wait {n} minutes", StepDefinitionType.When),
    ];

    private static readonly Dictionary<string, string[]> Slots = new(StringComparer.Ordinal)
    {
        ["n"] = ["5", "12", "-3", "300", "70000", "five", "1.5", ""],
        ["s"] = ["\"a b\"", "'x'", "\"\"", "plain"],
        ["w"] = ["name", "two words", "x-y"],
        ["f"] = ["1.5", "2", "-0.5", "abc", "1,5"],
        ["c"] = ["Red", "red", "DarkBlue", "Dark Blue", "Purple", "1"],
        ["m"] = ["$12", "12", "$x"],
        ["pl"] = ["", "s"],
        ["belly"] = ["belly", "stomach", "bag"],
    };

    private static readonly string[][] Tags = [[], ["admin"], ["other"]];

    private static readonly StepDefinitionType[] Kinds = [StepDefinitionType.Given, StepDefinitionType.When, StepDefinitionType.Then];

    [Fact]
    public void A_step_is_bound_as_reqnrolls_own_matcher_binds_it()
    {
        (StepMatcher passenger, StepDefinitionMatchService reqnroll, StepArgumentTypeConverter converter) = Matchers();
        Random random = new(Seed);
        List<string> differ = [];
        HashSet<string> seen = [];
        for (int i = 0; i < Drawn && differ.Count < 3; i++)
        {
            (string template, StepDefinitionType written) = Steps[random.Next(Steps.Length)];
            StepDefinitionType kind = random.Next(10) < 3 ? Kinds[random.Next(Kinds.Length)] : written;
            string text = Filled(template, random) + (random.Next(12) == 0 ? " today" : "");
            string[] tags = Tags[random.Next(Tags.Length)];
            string feature = random.Next(4) == 0 ? "Calculator" : "Shop";
            string ours = Passenger(passenger, kind, text, tags, feature);
            string theirs = Reqnroll(reqnroll, converter, kind, text, tags, feature);
            seen.Add(theirs.Split(':')[0]);
            if (ours != theirs)
            {
                differ.Add($"{kind} \"{text}\" tagged [{string.Join(", ", tags)}] in {feature}: Passenger {ours}, Reqnroll {theirs}");
            }
        }
        Assert.True(differ.Count == 0, $"Seed {Seed}: Passenger binds steps otherwise than Reqnroll's matcher:\n{string.Join("\n", differ)}");
        Assert.True(seen.IsSupersetOf(["Defined", "Ambiguous", "Undefined", "Refused", "Miscounted"]),
            $"The steps drawn came only to {string.Join(", ", seen)} in Reqnroll's matcher, too few outcomes to tell its rules apart");
    }

    private static string Filled(string template, Random random)
    {
        foreach ((string slot, string[] values) in Slots)
        {
            template = template.Replace("{" + slot + "}", values[random.Next(values.Length)], StringComparison.Ordinal);
        }
        return template;
    }

    /// <summary>What Passenger binds the step to: the one definition, those alike, or none; a definition bound only to fail
    /// counting its parameters is said as miscounted, one bound only to fail converting an argument as refused.</summary>
    private static string Passenger(StepMatcher matcher, StepDefinitionType kind, string text, string[] tags, string feature)
    {
        StepKind written = kind switch
        {
            StepDefinitionType.Given => StepKind.Given,
            StepDefinitionType.When => StepKind.When,
            _ => StepKind.Then,
        };
        StepMatch match = matcher.Match(written, text, false, false, new PassengerContext(feature, "Add", [.. tags.Select(t => "@" + t)]));
        return match.Status switch
        {
            MatchStatus.Defined when match.Miscounted is not null => $"Miscounted:{Assert.Single(match.Bindings).Binding.Method}",
            MatchStatus.Defined when match.Refused.Any(r => r.Fails) => $"Refused:{Assert.Single(match.Bindings).Binding.Method}",
            MatchStatus.Defined => $"Defined:{Assert.Single(match.Bindings).Binding.Method}",
            MatchStatus.Ambiguous => $"Ambiguous:{string.Join(",", match.Bindings.Select(b => b.Binding.Method).Order(StringComparer.Ordinal))}",
            _ => "Undefined",
        };
    }

    /// <summary>What Reqnroll's matcher binds the step to, in the same words: a definition bound whose argument its converter
    /// then refuses, or whose parameters' count is not its arguments', binds and fails as the step runs; one its parameters
    /// refuse alone binds and fails, several refused leave it unbound.</summary>
    private static string Reqnroll(StepDefinitionMatchService matcher, StepArgumentTypeConverter converter, StepDefinitionType kind, string text, string[] tags, string feature)
    {
        StepDefinitionKeyword keyword = kind switch
        {
            StepDefinitionType.Given => StepDefinitionKeyword.Given,
            StepDefinitionType.When => StepDefinitionKeyword.When,
            _ => StepDefinitionKeyword.Then,
        };
        StepInstance step = new(kind, keyword, kind + " ", text, new ReqnrollContext(feature, "Add", tags, Culture));
        BindingMatch match = matcher.GetBestMatch(step, Culture, out StepDefinitionAmbiguityReason reason, out List<BindingMatch> candidates);
        string[] methods = [.. candidates.Select(c => c.StepBinding.Method.Name).Distinct().Order(StringComparer.Ordinal)];
        if (match.Success)
        {
            IBindingParameter[] parameters = [.. match.StepBinding.Method.Parameters];
            string method = match.StepBinding.Method.Name;
            return parameters.Length != match.Arguments.Length ? $"Miscounted:{method}"
                : match.Arguments.Zip(parameters).All(each => converter.CanConvert(each.First.Value, each.Second.Type, Culture)) ? $"Defined:{method}" : $"Refused:{method}";
        }
        return reason switch
        {
            StepDefinitionAmbiguityReason.AmbiguousSteps or StepDefinitionAmbiguityReason.AmbiguousScopes => $"Ambiguous:{string.Join(",", methods)}",
            StepDefinitionAmbiguityReason.ParameterErrors when methods.Length == 1 => $"Refused:{methods[0]}",
            _ => "Undefined",
        };
    }

    /// <summary>Passenger's matcher and Reqnroll's over the definitions of <see cref="Calculator"/>, each read its own way, and
    /// the converter Reqnroll's is given.</summary>
    private static (StepMatcher Passenger, StepDefinitionMatchService Reqnroll, StepArgumentTypeConverter Converter) Matchers()
    {
        MethodInfo[] methods = typeof(Calculator).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        MethodInfo toMoney = typeof(Calculator).GetMethod(nameof(Calculator.ToMoney))!;

        BindingRegistry registry = new();
        registry.RegisterStepArgumentTransformationBinding(new StepArgumentTransformationBinding("\\$(\\d+)", new RuntimeBindingMethod(toMoney), "money", 0));
        CucumberExpressionParameterTypeRegistry types = new(registry);
        foreach (MethodInfo method in methods)
        {
            types.OnBindingMethodProcessed(new RuntimeBindingMethod(method));
        }
        BindingFactory factory = new(new StepDefinitionRegexCalculator(null!), new CucumberExpressionStepDefinitionBindingBuilderFactory(types), new CucumberExpressionDetector());
        List<StepBinding> read = [];
        foreach (MethodInfo method in methods.Where(m => m != toMoney))
        {
            ScopeAttribute? scope = method.GetCustomAttribute<ScopeAttribute>();
            foreach (StepDefinitionBaseAttribute step in method.GetCustomAttributes<StepDefinitionBaseAttribute>())
            {
                foreach (StepDefinitionType kind in step.Types)
                {
                    foreach (IStepDefinitionBinding built in factory.CreateStepDefinitionBindingBuilder(kind, new RuntimeBindingMethod(method),
                        scope is null ? null : new BindingScope(scope.Tag, scope.Feature, scope.Scenario), step.Expression, step.ExpressionType).Build())
                    {
                        registry.RegisterStepDefinitionBinding(built);
                    }
                }
                read.Add(new StepBinding(KindOf(step), step.Expression, method.Name, "Calc.Steps", [.. method.GetParameters().Select(ParameterOf)])
                {
                    Scopes = scope is null ? [] : [new StepScope(scope.Tag, scope.Feature, scope.Scenario)],
                });
            }
        }
        registry.Ready = true;
        StepArgumentTypeConverter converter = new(null!, registry, null!, null!);
        StepMatcher passenger = new(read, [new StepTransformation("\\$(\\d+)", "money", "Calc.Money") { Parameters = ["System.String"], Method = "Calculator.ToMoney" }], Culture);
        return (passenger, new StepDefinitionMatchService(registry, converter, null!, new MatchArgumentCalculator()), converter);
    }

    private static BindingKind KindOf(StepDefinitionBaseAttribute step) => step switch
    {
        GivenAttribute => BindingKind.Given,
        WhenAttribute => BindingKind.When,
        ThenAttribute => BindingKind.Then,
        _ => BindingKind.StepDefinition,
    };

    /// <summary>A method's parameter as Passenger reads one from code: an enum with its names, a type only a transformation
    /// makes, else .NET's own by its full name.</summary>
    private static StepParameter ParameterOf(ParameterInfo parameter) => parameter.ParameterType switch
    {
        { IsEnum: true } type => new StepParameter(parameter.Name!, "Calc." + type.Name, Enum.GetNames(type)) { Underlying = Enum.GetUnderlyingType(type).FullName },
        { } type when type == typeof(Money) => new StepParameter(parameter.Name!, "Calc.Money") { Unconverted = true },
        { } type => new StepParameter(parameter.Name!, type.FullName!),
    };

    public enum Color
    {
        Red,
        Green,
        DarkBlue,
    }

    public sealed class Money;

    /// <summary>The definitions both matchers are given.</summary>
    public static class Calculator
    {
        [StepArgumentTransformation("\\$(\\d+)", Name = "money")]
        public static Money ToMoney(string amount) => new();

        [Given("the first number is {int}")]
        public static void GivenNumber(int number) => GC.KeepAlive(number);

        [When("I enter {string} in the {word} field")]
        public static void WhenEnter(string value, string field) => GC.KeepAlive(value + field);

        [Then("the total is {float}")]
        public static void ThenTotal(float total) => GC.KeepAlive(total);

        [Given("I have (\\d+) cucumbers")]
        public static void GivenCucumbers(int count) => GC.KeepAlive(count);

        [Given("I have {int} cucumber(s) in my belly/stomach")]
        public static void GivenBelly(int count) => GC.KeepAlive(count);

        [Given("the colour is {Color}")]
        public static void GivenColour(Color color) => GC.KeepAlive(color);

        [Given("a price of {money}")]
        public static void GivenPrice(Money price) => GC.KeepAlive(price);

        [Given("a cost of (.*)")]
        public static void GivenCost(Money cost) => GC.KeepAlive(cost);

        [When("I log in")]
        public static void LogIn()
        {
        }

        [When("I log in")]
        [Scope(Tag = "admin")]
        public static void LogInAsAdmin()
        {
        }

        [When("I log in")]
        [Scope(Feature = "Calculator")]
        public static void LogInToCalculator()
        {
        }

        [Then("it repeats")]
        public static void RepeatOnce()
        {
        }

        [Then("it repeats")]
        public static void RepeatTwice()
        {
        }

        [Given("the count is (\\d+)")]
        public static void CountSmall(byte count) => GC.KeepAlive(count);

        [Given("the count is (.*)")]
        public static void CountAny(string count) => GC.KeepAlive(count);

        [Given("the size is (\\d+)")]
        public static void SizeByte(byte size) => GC.KeepAlive(size);

        [Given("the size is (\\d+)")]
        public static void SizeShort(short size) => GC.KeepAlive(size);

        [StepDefinition("the calculator is cleared")]
        public static void Clear()
        {
        }

        [When("I wait (\\d+) seconds")]
        public static void Wait(int seconds, int extra) => GC.KeepAlive(seconds + extra);

        [When("I wait (\\d+) minutes")]
        public static void WaitMinutes()
        {
        }
    }
}
