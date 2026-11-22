// Translated from behavioral/sns/check_delete_platform_application_forecloses_new_endpoints.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Numerics;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.delete-platform-application-forecloses-new-endpoints</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletePlatformApplicationForeclosesNewEndpoints
{
    /// <summary>
    /// CreatePlatformEndpoint and GetPlatformApplicationAttributes both fail NotFound once the
    /// owning platform application is deleted, while a fresh application of the same name admits
    /// the identical CreatePlatformEndpoint call.
    /// </summary>
    [Test]
    [DisplayName("sns.delete-platform-application-forecloses-new-endpoints")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-platapp-");
        var appArn = await CreateApp(sns, name);
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        // Positive control: the live application admits CreatePlatformEndpoint.
        var liveEndpointArn = await CreateEndpoint(sns, appArn);
        ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = liveEndpointArn }));
        var attrs = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("Enabled") == "true",
            $"positive control: expected the live application to report Enabled='true', got {Py.Repr(attrs)}");

        // Break exactly one thing: delete the endpoint, then the application that owned it.
        await sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = liveEndpointArn });
        await sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn });

        var (code, message) = await PollNotFound(
            () => sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn }),
            description: "the deleted application to read back NotFound");
        Ensure(code == "NotFound", $"expected the deleted application to read back NotFound, got {Py.Repr(code)}");
        Ensure(message == "PlatformApplication does not exist",
            $"unexpected NotFound message reading the deleted application: {Py.Repr(message)}");

        var deadChannelId = ChannelId();
        var (createCode, createMessage) = await Error(() => sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn,
            Token = deadChannelId,
            Attributes = new() { ["ChannelId"] = deadChannelId, ["UserId"] = Guid.NewGuid().ToString() },
        }));
        Ensure(createCode == "NotFound",
            $"CreatePlatformEndpoint under a deleted application: expected NotFound, got {Py.Repr(createCode)}");
        Ensure(createMessage == "PlatformApplication does not exist",
            $"unexpected NotFound message for CreatePlatformEndpoint under a deleted application: {Py.Repr(createMessage)}");

        // The NotFound is specific to this now-dead application, not a blanket refusal: a fresh
        // application of the same name admits the identical kind of call.
        var freshArn = await CreateApp(sns, name);
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = freshArn }));
        var freshEndpointArn = await CreateEndpoint(sns, freshArn);
        ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = freshEndpointArn }));
        var freshAttrs = (await sns.GetEndpointAttributesAsync(new GetEndpointAttributesRequest { EndpointArn = freshEndpointArn })).Attributes ?? [];
        Ensure(freshAttrs.GetValueOrDefault("Enabled") == "true",
            $"expected the endpoint under the fresh application to report Enabled='true', got {Py.Repr(freshAttrs)}");
    }

    /// <summary><c>str(uuid.uuid4().int)[:18]</c>: a numeric Baidu channel id.</summary>
    private static string ChannelId() => new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true).ToString()[..18];

    /// <summary>
    /// A BAIDU platform application (accepts an arbitrary, unvalidated key pair, unlike GCM/ADM
    /// which reject a fabricated credential synchronously); returns its ARN.
    /// </summary>
    private static async Task<string> CreateApp(AmazonSimpleNotificationServiceClient sns, string name) =>
        (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = name,
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;

    /// <summary>
    /// A real endpoint under a BAIDU application. Baidu Cloud Push requires a numeric ChannelId
    /// that the Token repeats, plus a UserId, or real AWS rejects the create as a blank field.
    /// </summary>
    private static async Task<string> CreateEndpoint(AmazonSimpleNotificationServiceClient sns, string appArn)
    {
        var channelId = ChannelId();
        return (await sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn,
            Token = channelId,
            Attributes = new() { ["ChannelId"] = channelId, ["UserId"] = Guid.NewGuid().ToString() },
        })).EndpointArn;
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }

    /// <summary>
    /// Poll fn() until it raises NotFound; raise on timeout. Read-after-delete is eventually
    /// consistent, so a bare single read cannot tell a correct target from a lagging one.
    /// </summary>
    private static async Task<(string? Code, string? Message)> PollNotFound<T>(Func<Task<T>> fn, double deadlineS = 30.0, double pollS = 2.0, string description = "")
        where T : class
    {
        var deadline = FidelityTime.StartNew();
        string last;
        while (true)
        {
            var (_, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                last = "'success'";
            }
            else
            {
                last = Py.Repr(new object?[] { error.Code, error.Message });
                if (error.Code == "NotFound")
                {
                    return (error.Code, error.Message);
                }
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(deadlineS))
            {
                throw Fail($"timed out after {deadlineS}s waiting for {description}, last saw {last}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }
}
