using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace Greenhouse.Mobile;

/// <summary>A plant on a bench, how wet its pot is (0 dry, 1 full) and how often it is watered.</summary>
public sealed record Plant(string Name, string Bench, double Moisture, int EveryDays)
{
    public bool Thirsty => Moisture < 0.3;

    public string Schedule => $"{Bench} bench, every {EveryDays} days";
}

/// <summary>The greenhouse's plants and the round that waters the thirsty ones.</summary>
public sealed class PlantsViewModel : INotifyPropertyChanged
{
    public PlantsViewModel()
    {
        WaterCommand = new Command(Water);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Plant> Plants { get; } =
    [
        new("Basil", "North", 0.2, 2),
        new("Tomato", "South", 0.65, 3),
        new("Fig", "South", 0.9, 8),
        new("Fern", "Shade", 0.45, 3),
        new("Mint", "North", 0.15, 2),
        new("Chilli", "South", 0.55, 4),
        new("Lemon", "South", 0.8, 6),
        new("Orchid", "Shade", 0.35, 7),
    ];

    public string Summary => $"{Plants.Count(p => p.Thirsty)} of {Plants.Count} plants need water today";

    public ICommand WaterCommand { get; }

    private void Water()
    {
        for (int i = 0; i < Plants.Count; i++)
        {
            if (Plants[i].Thirsty)
            {
                Plants[i] = Plants[i] with { Moisture = 1 };
            }
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
}
