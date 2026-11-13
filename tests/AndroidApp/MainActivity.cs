namespace AndroidApp;

/// <summary>Says what it worked out in the device's log, where Run shows it, and stops at its breakpoint under Debug.</summary>
[Activity(Label = "Passenger's test app", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        int answer = 6 * 7;
        string greeting = $"Hello from Passenger's test app, {answer}";
        SetContentView(new TextView(this) { Text = greeting });
        Android.Util.Log.Info("PassengerTest", greeting);
    }
}
