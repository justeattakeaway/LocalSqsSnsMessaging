// Translated from behavioral/sns/check_create_platform_endpoint_nonexistent_app_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-platform-endpoint-nonexistent-app-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreatePlatformEndpointNonexistentAppFails
{
    /// <summary>
    /// CreatePlatformEndpoint under a nonexistent PlatformApplicationArn fails with NotFound,
    /// while the same call against a real application fails for another reason, never NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.create-platform-endpoint-nonexistent-app-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // BAIDU accepts an arbitrary API-key/secret-key pair at create time; GCM/ADM reject fake
        // credentials and APNS rejects a non-Apple certificate.
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-platapp-"),
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = $"baidu-api-key-{Hex8()}",
                ["PlatformCredential"] = $"baidu-secret-key-{Hex8()}",
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        // Baidu Cloud Push refuses the fabricated token, a reason unrelated to app existence.
        var (realAppCode, _, realEndpointArn) = await TryCreateEndpoint(() => sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn, Token = $"token-{Guid.NewGuid()}",
        }));
        if (!string.IsNullOrEmpty(realEndpointArn))
        {
            ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = realEndpointArn }));
        }

        Ensure(realAppCode != "NotFound",
            $"CreatePlatformEndpoint against a REAL app must not be NotFound, got {Py.Repr(realAppCode)} " +
            $"(endpoint_arn={Py.Repr(realEndpointArn)})");

        var ghost = BogusPlatformAppArn(ctx);
        var (ghostCode, ghostMessage, ghostEndpointArn) = await TryCreateEndpoint(() => sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = ghost, Token = $"token-{Guid.NewGuid()}",
        }));
        if (!string.IsNullOrEmpty(ghostEndpointArn))
        {
            ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = ghostEndpointArn }));
        }

        Ensure(ghostCode == "NotFound", $"CreatePlatformEndpoint: expected NotFound, got {Py.Repr(ghostCode)}");
        Ensure(ghostMessage == "PlatformApplication does not exist",
            $"unexpected NotFound message for CreatePlatformEndpoint under a missing app: {Py.Repr(ghostMessage)}");
    }

    private static string Hex8() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Call fn(); return (error_code, error_message, endpoint_arn). The code and message are None
    /// on success, when the endpoint ARN is returned so the caller can clean it up.
    /// </summary>
    private static async Task<(string? Code, string? Message, string? EndpointArn)> TryCreateEndpoint(Func<Task<CreatePlatformEndpointResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null ? (null, null, result!.EndpointArn) : (error.Code, error.Message, null);
    }

    /// <summary>A well-formed but nonexistent PlatformApplicationArn in the caller's account/region.</summary>
    private static string BogusPlatformAppArn(FidelityContext ctx, string prefix = "sim2real-behav-ghost-app-") =>
        $"arn:aws:sns:{ctx.Region}:{ctx.Account}:app/GCM/{prefix}{Hex8()}";
}
