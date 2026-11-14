// A minimal API that asks its own handlers once it listens, so a test stops in them with no client of its own.
WebApplication app = WebApplication.Create(args);
app.Urls.Add("http://127.0.0.1:0");

app.MapGet("/add/{a:int}/{b:int}", (int a, int b) =>
{
    int sum = a + b;
    return Results.Ok(sum);
});

app.MapGet("/later/{n:int}", async (int n) =>
{
    await Task.Yield();
    int twice = n * 2;
    return Results.Ok(twice);
});

await app.StartAsync();
string address = app.Urls.First();
using HttpClient client = new();
Console.WriteLine($"added {await client.GetStringAsync($"{address}/add/2/3")}");
Console.WriteLine($"later {await client.GetStringAsync($"{address}/later/4")}");
await app.StopAsync();
