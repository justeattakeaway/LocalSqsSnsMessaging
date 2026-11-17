// Translated from behavioral/sns/check_set_platform_application_attributes_unknown_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-platform-application-attributes-unknown-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetPlatformApplicationAttributesUnknownKeyRejected
{
    /// <summary>
    /// SetPlatformApplicationAttributes refuses an unknown attribute name with InvalidParameter
    /// and never stores it, while a recognized attribute name is accepted.
    /// </summary>
    [Test]
    [DisplayName("sns.set-platform-application-attributes-unknown-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        // GCM and ADM validate credentials at creation; BAIDU accepts arbitrary keys, so the
        // check exercises attribute-name validation rather than credential validation.
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

        // Only acceptance is checked: SNS appears to keep Enabled pinned for a BAIDU app with
        // fake credentials, so the value need not flip.
        await sns.SetPlatformApplicationAttributesAsync(new SetPlatformApplicationAttributesRequest
        {
            PlatformApplicationArn = appArn, Attributes = new() { ["Enabled"] = "false" },
        });

        var (code, message) = await Error(() => sns.SetPlatformApplicationAttributesAsync(new SetPlatformApplicationAttributesRequest
        {
            PlatformApplicationArn = appArn,
            Attributes = new() { ["NotARealPlatformAttributeXYZ"] = "somevalue" },
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got {Py.Repr(code)}");
        Ensure(message == "Invalid parameter: Attributes Reason: Invalid attribute name: NotARealPlatformAttributeXYZ",
            $"expected the exact message 'Invalid parameter: Attributes Reason: Invalid attribute name: NotARealPlatformAttributeXYZ', got {Py.Repr(message)}");

        var attrs = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("NotARealPlatformAttributeXYZ"),
            $"bogus attribute key was persisted on the resource: {Py.Repr(attrs)}");
        Ensure(attrs.GetValueOrDefault("Enabled") is "true" or "false",
            $"Enabled attribute missing or malformed after the rejected call: {Py.Repr(attrs.GetValueOrDefault("Enabled"))}");
    }

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message ?? "");
    }
}
