// What Smart Step Into and the debugger's synthetic variables ($exception, the values calls return) are tried on. Tests
// find a line by the code on it, so each case's lines are its own; a new case goes below the others.
Calls.Run();
Lambdas.Run();
Awaits.RunAsync().GetAwaiter().GetResult();
Returns.Run();
Throws.Run();
Queries.Run();
Chooses.Run();
Arguments.Run();
Natives.Run();

internal static class Calls
{
    public static void Run()
    {
        int nested = Outer(Inner());
        int sum = Add(Add(1, 2), 3);
        Console.WriteLine($"nested {nested} {sum}");
    }

    private static int Inner() => 20;

    private static int Outer(int value)
    {
        return value + 1;
    }

    private static int Add(int a, int b)
    {
        return a + b;
    }
}

internal static class Lambdas
{
    public static void Run()
    {
        int[] doubled = [.. Numbers().Select(n => Twice(n))];
        Console.WriteLine($"doubled {doubled.Length}");
    }

    private static int[] Numbers() => [1, 2, 3];

    private static int Twice(int n)
    {
        return n * 2;
    }
}

internal static class Awaits
{
    public static async Task RunAsync()
    {
        int computed = await ComputeAsync(Seed());
        Console.WriteLine($"computed {computed}");
    }

    private static int Seed() => 4;

    private static async Task<int> ComputeAsync(int value)
    {
        await Task.Yield();
        return value * 3;
    }
}

internal static class Returns
{
    public static void Run()
    {
        string described = Describe(Count());
        double ratio = Ratio(3, 4);
        Spot spot = Locate(2, 3);
        Console.WriteLine($"{described} {ratio} {spot}");
    }

    private static int Count() => 42;

    private static string Describe(int count) => $"count {count}";

    private static double Ratio(int part, int whole) => (double)part / whole;

    private static Spot Locate(int x, int y) => new(x, y);
}

internal static class Throws
{
    public static void Run()
    {
        try
        {
            Fail("bad input");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("caught");
        }
    }

    private static void Fail(string reason) => throw new InvalidOperationException(reason);
}

internal static class Queries
{
    public static void Run()
    {
        int[] numbers = [1, 2, 3, 4, 5];
        int counted = numbers.Count(n => n > 2);
        IEnumerable<int> unread = numbers.Where(n => n > 3);
        Console.WriteLine($"counted {counted}");
    }
}

internal readonly record struct Spot(int X, int Y);

internal static class Chooses
{
    public static void Run()
    {
        for (int round = 0; round < 2; round++)
        {
            bool ok = false;
            int chosen = ok ? Pick(1) : Pick(2);
            Console.WriteLine($"chosen {chosen}");
        }
    }

    private static int Pick(int value)
    {
        return value * 10;
    }
}

internal static class Arguments
{
    public static void Run()
    {
        int a = 1;
        int b = 2;
        int first = Add(a, 1);
        int total = Add(Twice(a), Twice(b));
        Console.WriteLine($"arguments {first} {total}");
    }

    private static int Add(int x, int y)
    {
        int added = x + y;
        return added;
    }

    private static int Twice(int value)
    {
        return value * 2;
    }
}

internal static class Natives
{
    public static void Run()
    {
        nint signed = Signed();
        nuint unsigned = Unsigned();
        Console.WriteLine($"natives {signed} {unsigned}");
    }

    private static nint Signed() => -42;

    private static nuint Unsigned() => 4000000000;
}
