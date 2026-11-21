// Translated from behavioral/sns/check_list_endpoints_by_platform_application_tracks_create_and_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Numerics;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-endpoints-by-platform-application-tracks-create-and-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListEndpointsByPlatformApplicationTracksCreateAndDelete
{
    /// <summary>
    /// ListEndpointsByPlatformApplication reflects an endpoint right after it is created and
    /// drops it right after it is deleted, while a NextToken SNS never issued is refused.
    /// </summary>
    [Test]
    [DisplayName("sns.list-endpoints-by-platform-application-tracks-create-and-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // BAIDU accepts an arbitrary, unvalidated key pair unlike GCM/ADM, which reject a fabricated
        // credential synchronously.
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-platapp-"),
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        var before = await ListedEndpointArns(sns, appArn);
        Ensure(before.Count == 0, $"expected a freshly created application to list no endpoints, got {ShowSet(before)}");

        // Baidu Cloud Push requires a numeric ChannelId that the Token repeats, plus a UserId, or
        // real AWS rejects the create as a blank field.
        var channelId = new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true).ToString()[..18];
        var endpointArn = (await sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn,
            Token = channelId,
            Attributes = new() { ["ChannelId"] = channelId, ["UserId"] = Guid.NewGuid().ToString() },
        })).EndpointArn;
        ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn }));

        var afterCreate = await Poll(() => ListedEndpointArns(sns, appArn), arns => arns.Contains(endpointArn),
            timeoutS: 30.0, intervalS: 2.0,
            description: "the created endpoint to appear in ListEndpointsByPlatformApplication");
        Ensure(afterCreate.SetEquals([endpointArn]),
            $"expected the listing to hold exactly the one endpoint created here, got {ShowSet(afterCreate)}");

        // Break exactly one thing: a NextToken SNS never issued.
        var (result, error) = await FidelityContext.Capture(() => sns.ListEndpointsByPlatformApplicationAsync(new ListEndpointsByPlatformApplicationRequest
        {
            PlatformApplicationArn = appArn, NextToken = "sim2real-behav-not-a-real-token",
        }));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        Ensure(error.Code == "InvalidParameter", $"forged NextToken: expected InvalidParameter, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "Invalid parameter: NextToken Reason: token is invalid or expired",
            $"unexpected message for a forged NextToken: {Py.Repr(error.Message)}");

        // The rejected call disturbed nothing: the real listing still holds the one endpoint.
        var stillThere = await ListedEndpointArns(sns, appArn);
        Ensure(stillThere.SetEquals([endpointArn]),
            $"the forged-token call must not have changed the listing, got {ShowSet(stillThere)}");

        await sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn });
        var afterDelete = await Poll(() => ListedEndpointArns(sns, appArn), arns => !arns.Contains(endpointArn),
            timeoutS: 30.0, intervalS: 2.0,
            description: "the deleted endpoint to leave ListEndpointsByPlatformApplication");
        Ensure(afterDelete.Count == 0,
            $"expected the listing to be empty again after the only endpoint was deleted, got {ShowSet(afterDelete)}");
    }

    private static string ShowSet(HashSet<string> set) =>
        set.Count == 0 ? "set()" : "{" + string.Join(", ", set.Select(Py.Repr)) + "}";

    /// <summary>Every EndpointArn a paginated ListEndpointsByPlatformApplication walk returns.</summary>
    private static async Task<HashSet<string>> ListedEndpointArns(AmazonSimpleNotificationServiceClient sns, string appArn)
    {
        var output = new HashSet<string>();
        string? token = null;
        do
        {
            var page = await sns.ListEndpointsByPlatformApplicationAsync(new ListEndpointsByPlatformApplicationRequest
            {
                PlatformApplicationArn = appArn, NextToken = token,
            });
            output.UnionWith((page.Endpoints ?? []).Select(e => e.EndpointArn));
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return output;
    }

    /// <summary>Poll fn() until pred(value) holds; raise on timeout.</summary>
    private static async Task<HashSet<string>> Poll(Func<Task<HashSet<string>>> fn, Func<HashSet<string>, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: condition not met within {timeoutS}s, last saw {ShowSet(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }
}
