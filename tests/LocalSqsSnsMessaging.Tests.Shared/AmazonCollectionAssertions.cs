using System.Diagnostics;
using Amazon;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public static class AmazonCollectionAssertions
{
    [DebuggerStepThrough]
    public static void ShouldBeEmptyAwsCollection<T>(this IEnumerable<T> awsCollection)
    {
        if (AWSConfigs.InitializeCollections)
        {
            awsCollection.ShouldBeEmpty();
        }
        else
        {
            awsCollection.ShouldBeNull();
        }
    }
}
