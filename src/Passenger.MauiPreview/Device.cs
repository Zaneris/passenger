using System.Reflection;
using Microsoft.Maui.Devices;

namespace Passenger.MauiPreview;

/// <summary>The device MAUI's OnPlatform and OnIdiom read: the platform and idiom a request is drawn as.</summary>
internal sealed class PreviewDevice(DevicePlatform platform, DeviceIdiom idiom) : IDeviceInfo
{
    private static (string Platform, string Idiom)? current;

    public string Model => "Preview";

    public string Manufacturer => "Passenger";

    public string Name => "Preview";

    public string VersionString => "1.0";

    public Version Version => new(1, 0);

    public DevicePlatform Platform => platform;

    public DeviceIdiom Idiom => idiom;

    public DeviceType DeviceType => DeviceType.Virtual;

    /// <summary>Made MAUI's current device, as only MAUI's own internal setter lets anything say; a MAUI without it reads
    /// its own device, which the previewer says once.</summary>
    public static void Use(string platform, string idiom)
    {
        if (current == (platform, idiom))
        {
            return;
        }
        current = (platform, idiom);
        MethodInfo? set = typeof(DeviceInfo).GetMethod("SetCurrent", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IDeviceInfo)]);
        if (set is null)
        {
            Console.Error.WriteLine("This MAUI lets no device be set: OnPlatform and OnIdiom read its own");
            return;
        }
        set.Invoke(null, [new PreviewDevice(DevicePlatform.Create(platform), DeviceIdiom.Create(idiom))]);
    }
}
