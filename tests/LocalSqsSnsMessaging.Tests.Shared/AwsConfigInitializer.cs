#pragma warning disable CA2255
using System.Runtime.CompilerServices;
using Amazon;

namespace LocalSqsSnsMessaging.Tests;

internal static class AwsConfigInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        AWSConfigs.InitializeCollections = true;
    }
}
