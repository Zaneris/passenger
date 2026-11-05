namespace GreenhouseGame;

/// <summary>The greenhouse: its rain tank, the day it is, and the sprinkler that waters the benches each morning.</summary>
public partial class Greenhouse : Node2D
{
    /// <summary>What the rain tank holds now.</summary>
    [Export]
    public float TankLitres { get; set; } = 120f;

    /// <summary>A day's length, in seconds of the game.</summary>
    [Export]
    public float DayLength { get; set; } = 1.5f;

    public int Day { get; private set; } = 1;

    public int DaysOfWaterLeft { get; private set; }

    private double clock;
    private Node2D sprinkler = null!;
    private ProgressBar tank = null!;
    private Label today = null!;

    public override void _Ready()
    {
        sprinkler = GetNode<Node2D>("Sprinkler");
        tank = GetNode<ProgressBar>("Tank");
        today = GetNode<Label>("Today");
    }

    public override void _Process(double delta)
    {
        clock += delta;
        if (clock >= DayLength)
        {
            clock -= DayLength;
            Day++;
            TankLitres = Mathf.Max(TankLitres - sprinkler.Call("spray").AsSingle(), 0f);
        }
        DaysOfWaterLeft = Forecast();
        tank.Value = TankLitres;
        today.Text = $"Day {Day}: {TankLitres:0.0} litres, {DaysOfWaterLeft} days of water left";
    }

    /// <summary>Whole days the tank lasts at what each plant says it will drink.</summary>
    public int Forecast()
    {
        float daily = 0f;
        foreach (Node plant in GetNode("Benches").GetChildren())
        {
            daily += plant.Call("daily_litres").AsSingle();
        }
        int days = 0;
        for (float left = TankLitres; left > daily && days < 10_000; left -= daily)
        {
            days++;
        }
        return days;
    }
}
