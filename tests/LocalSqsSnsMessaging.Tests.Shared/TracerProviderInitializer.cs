#pragma warning disable CA2255
using System.Runtime.CompilerServices;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace LocalSqsSnsMessaging.Tests;

internal static class TracerProviderInitializer
{
    // Stored to prevent garbage collection; disposed on process exit.
    internal static TracerProvider? TracerProvider;

    [ModuleInitializer]
    public static void Initialize()
    {
        TracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("LocalSqsSnsMessaging")
            .AddAWSInstrumentation()
            .Build();
    }
}
