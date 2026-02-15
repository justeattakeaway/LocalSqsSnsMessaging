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

    /// <summary>
    /// Asserts a collection is either null or empty. Use this when real AWS returns
    /// AlwaysSendDictionary (non-null) but in-memory may return null.
    /// </summary>
    [DebuggerStepThrough]
    public static void ShouldBeNullOrEmptyAwsCollection<T>(this IEnumerable<T>? awsCollection)
    {
        if (awsCollection is not null)
        {
            awsCollection.ShouldBeEmpty();
        }
    }
}
