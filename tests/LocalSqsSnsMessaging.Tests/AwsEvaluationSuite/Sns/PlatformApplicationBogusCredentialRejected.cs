// Translated from behavioral/sns/check_platform_application_bogus_credential_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.platform-application-bogus-credential-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PlatformApplicationBogusCredentialRejected
{
    /// <summary>
    /// CreatePlatformApplication refuses a bogus GCM PlatformCredential with InvalidParameter
    /// and creates nothing, while a BAIDU application is accepted as a positive control.
    /// </summary>
    [Test]
    [DisplayName("sns.platform-application-bogus-credential-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "Kept deliberately: platform credentials are never checked against the push service, so tests can use fake ones; a bogus GCM credential is accepted instead of refused")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // SNS does not validate BAIDU credentials at creation, so this app must be accepted.
        var controlName = FidelityContext.UniqueName("sim2real-behav-sns-control-");
        var controlArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = controlName,
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = "sim2real-fake-api-key",
                ["PlatformCredential"] = "sim2real-fake-secret-key",
                ["Enabled"] = "false",
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = controlArn }));
        var controlAttrs = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest
        {
            PlatformApplicationArn = controlArn,
        })).Attributes ?? [];
        Ensure(controlAttrs.GetValueOrDefault("Enabled") == "false",
            "positive control: expected the BAIDU app's Enabled=false to round-trip " +
            "(proving CreatePlatformApplication itself works in this account), got " +
            $"{Py.Repr(controlAttrs.GetValueOrDefault("Enabled"))}");

        var appName = FidelityContext.UniqueName("sim2real-behav-sns-badapp-");
        var (code, message) = await Error(() => sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = appName,
            Platform = "GCM",
            Attributes = new() { ["PlatformCredential"] = "definitely-not-a-real-fcm-credential" },
        }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for bogus GCM PlatformCredential, got {code}");
        var expected = "Invalid parameter: Attributes Reason: Platform credentials are invalid";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var wouldBeArn = $"arn:aws:sns:{ctx.Region}:{ctx.Account}:app/GCM/{appName}";
        var (notFoundCode, notFoundMessage) = await Error(() =>
            sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest { PlatformApplicationArn = wouldBeArn }));
        Ensure(notFoundCode == "NotFound",
            $"expected NotFound for the never-created platform application, got {notFoundCode}");
        expected = "PlatformApplication does not exist";
        Ensure(notFoundMessage == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(notFoundMessage)}");
    }

    /// <summary>Return (code, message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}
