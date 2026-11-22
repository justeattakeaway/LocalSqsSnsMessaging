using Shouldly;

namespace InMemoryAws.Tests.Packaging;

/// <summary>
/// The build integration as NuGet delivers it: <c>buildTransitive/InMemoryAws.targets</c> reaches every project that
/// gets the package, directly or through a project reference, and adds the AWS SDK glue to those that reference the
/// AWS SDK.
/// </summary>
public sealed class PackageConsumptionTests
{
    [ClassDataSource<PackageConsumer>(Shared = SharedType.PerTestSession)]
    public required PackageConsumer Consumer { get; init; }

    [Test]
    public async Task AProjectThatReferencesThePackageGetsTheClients()
    {
        var run = await Consumer.RunAsync("Direct", "run");

        run.ShouldSucceed();
        run.Output.ShouldContain("https://sqs.eu-west-1.amazonaws.com/000000000000/orders placed");
    }

    [Test]
    public async Task AProjectThatGetsThePackageThroughAProjectReferenceGetsTheClients()
    {
        var run = await Consumer.RunAsync(Path.Combine("Transitive", "Tests"), "run");

        run.ShouldSucceed();
        run.Output.ShouldContain("https://sqs.eu-west-1.amazonaws.com/000000000000/orders placed");
    }

    [Test]
    public async Task OnlyAProjectThatReferencesTheAwsSdkGetsTheSources()
    {
        (await Consumer.SdkSourcesCompiledAsync(Path.Combine("NoAwsSdk", "Lib"))).ShouldBeEmpty();
        (await Consumer.SdkSourcesCompiledAsync(Path.Combine("NoAwsSdk", "Downstream"))).ShouldBeEmpty();
        (await Consumer.SdkSourcesCompiledAsync(Path.Combine("NoAwsSdk", "DownstreamWithSdk")))
            .ShouldBe(["InMemoryAws.AwsSdk.cs", "InMemoryAws.Sqs.cs"]);
    }

    [Test]
    public async Task AProjectThatSeesAnotherCopysInternalsOptsOutToUseIt()
    {
        // Shared has InternalsVisibleTo Tests, so Tests sees both its own copy and Shared's: the calls are ambiguous.
        var clash = await Consumer.RunAsync(Path.Combine("InternalsVisibleTo", "Tests"), "build", "--no-restore");
        clash.ExitCode.ShouldNotBe(0);
        clash.Output.ShouldContain("CS0121");

        // With InMemoryAwsSdkSources false, Tests compiles none and calls Shared's.
        var optedOut = await Consumer.RunAsync(Path.Combine("InternalsVisibleTo", "Tests"), "run", "-p:OptOut=true");
        optedOut.ShouldSucceed();
        optedOut.Output.ShouldContain("https://sqs.us-east-1.amazonaws.com/000000000000/orders");
    }
}
