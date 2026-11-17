// Translated from behavioral/sns/check_platform_application_custom_attribute_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.platform-application-custom-attribute-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PlatformApplicationCustomAttributeRoundTrip
{
    /// <summary>
    /// SetPlatformApplicationAttributes on a BAIDU platform application with an unvalidated
    /// credential is accepted, but GetPlatformApplicationAttributes keeps reporting Enabled='true'.
    /// </summary>
    [Test]
    [DisplayName("sns.platform-application-custom-attribute-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = FidelityContext.UniqueName("sim2real-platform-app-"),
            // ADM/GCM/APNS reject a fabricated credential at creation; BAIDU accepts any key pair.
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        var before = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(before.GetValueOrDefault("Enabled") == "true",
            $"expected a freshly created platform application to report Enabled='true', got {Py.Repr(before)}");

        // Accepted with no ClientError, even though the platform credential was never
        // validated against a live push provider.
        await sns.SetPlatformApplicationAttributesAsync(new SetPlatformApplicationAttributesRequest
        {
            PlatformApplicationArn = appArn,
            Attributes = new() { ["Enabled"] = "false" },
        });

        // Independent read via a different operation than the one that "mutated": the flip
        // never actually took effect.
        var after = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = appArn })).Attributes ?? [];
        Ensure(after.GetValueOrDefault("Enabled") == "true",
            "expected SetPlatformApplicationAttributes on an unvalidated-credential platform " +
            $"application to be a silent no-op (Enabled stays 'true'), got {Py.Repr(after)}");
    }
}
