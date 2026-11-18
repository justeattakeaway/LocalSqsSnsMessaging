#pragma warning disable CA2255
using System.Runtime.CompilerServices;
using Amazon;

namespace InMemoryAws.Tests;

internal static class AwsConfigInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // This lets us easily test between initialized and uninitialized collections.
        AWSConfigs.InitializeCollections = false;
    }
}
