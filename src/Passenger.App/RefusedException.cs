using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A refusal thrown, its words kept: the window says them in the person's language (<see cref="Why"/>), its
/// message in English, as an agent's answer and a log say it.</summary>
public sealed class RefusedException(Words why) : InvalidOperationException(why.English)
{
    public Words Why { get; } = why;

    /// <summary>What a failure says to the person: a refusal's or a launch's words, in whatever language they are said, else
    /// its message as written.</summary>
    public static Words Of(Exception failure) => failure switch
    {
        RefusedException refused => refused.Why,
        LaunchException launch => launch.Why,
        _ => failure.Message,
    };
}
