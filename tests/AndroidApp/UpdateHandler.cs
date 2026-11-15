[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(AndroidApp.UpdateHandler))]

namespace AndroidApp;

/// <summary>Says in the device's log each Hot Reload update its app is told of, naming the types the update changed.</summary>
internal static class UpdateHandler
{
    internal static void UpdateApplication(Type[]? types) =>
        Android.Util.Log.Info("PassengerTest", $"Update handler called for {string.Join(", ", types?.Select(t => t.Name) ?? [])}");
}
