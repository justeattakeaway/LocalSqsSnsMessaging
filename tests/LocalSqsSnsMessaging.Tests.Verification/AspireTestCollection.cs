namespace LocalSqsSnsMessaging.Tests.Verification;

[CollectionDefinition(Name)]
#pragma warning disable CA1711
public sealed class AspireTestCollection : ICollectionFixture<AspireFixture>
#pragma warning restore CA1711
{
    public const string Name = "Aspire Test Collection";
}