// Translated from behavioral/sns/check_set_endpoint_attributes_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-endpoint-attributes-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetEndpointAttributesNotFound
{
    /// <summary>
    /// SetEndpointAttributes on a nonexistent EndpointArn fails with NotFound and the exact message
    /// 'Endpoint does not exist', while the same call on the check's own live endpoint is accepted,
    /// so the NotFound is specific to the absent endpoint.
    /// </summary>
    [Test]
    [DisplayName("sns.set-endpoint-attributes-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // Baidu Cloud Push ("BAIDU") accepts an arbitrary API-key/secret-key pair with no
        // live/format validation, unlike GCM/ADM which synchronously reject a fabricated
        // credential, so a real, live EndpointArn exists to use as the positive control.
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-platapp-"),
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        // Baidu additionally requires ChannelId and UserId attributes, and the Token
        // field itself must repeat the ChannelId (per the CreatePlatformEndpoint model
        // docs), or real AWS rejects the endpoint creation with a blank-field error.
        // ChannelId must be purely numeric, or real AWS rejects it as malformed.
        var channelId = UuidInt()[..18];
        var realArn = (await sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn,
            Token = channelId,
            Attributes = new() { ["ChannelId"] = channelId, ["UserId"] = FidelityContext.UniqueName("sim2real-behav-user-") },
        })).EndpointArn;
        ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = realArn }));

        // Positive control: SetEndpointAttributes on the real, just-created endpoint is
        // accepted with no ClientError -- otherwise a target that rejects every
        // SetEndpointAttributes call would pass the negative below for the wrong reason.
        await sns.SetEndpointAttributesAsync(new SetEndpointAttributesRequest { EndpointArn = realArn, Attributes = new() { ["Enabled"] = "false" } });

        // Break exactly one thing: derive the bogus ARN by swapping only the real
        // endpoint's own trailing id for a fresh one, keeping every other segment
        // (partition, region, account, platform, app name) intact -- an ARN that
        // merely APPENDS a suffix is malformed and draws InvalidParameter instead of
        // the NotFound this check exists to measure.
        var ghost = realArn[..realArn.LastIndexOf('/')] + "/" + Guid.NewGuid();
        var (code, message) = await Error(() =>
            sns.SetEndpointAttributesAsync(new SetEndpointAttributesRequest { EndpointArn = ghost, Attributes = new() { ["Enabled"] = "false" } }));
        Ensure(code == "NotFound", $"SetEndpointAttributes: expected NotFound, got {code}");
        Ensure(message == "Endpoint does not exist",
            $"expected the exact message 'Endpoint does not exist', got {Py.Repr(message)}");
    }

    /// <summary>str(uuid.uuid4().int): a random uuid as a decimal integer.</summary>
    private static string UuidInt() =>
        new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true).ToString(CultureInfo.InvariantCulture);

    /// <summary>Call fn(); return the botocore error (Code, Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}
