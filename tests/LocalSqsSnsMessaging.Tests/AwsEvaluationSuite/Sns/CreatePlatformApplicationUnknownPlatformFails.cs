// Translated from behavioral/sns/check_create_platform_application_unknown_platform_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-platform-application-unknown-platform-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreatePlatformApplicationUnknownPlatformFails
{
    /// <summary>
    /// CreatePlatformApplication with an unknown Platform is refused with InvalidParameter and
    /// lists nothing, while a BAIDU application in the same run is accepted and listed.
    /// </summary>
    [Test]
    [DisplayName("sns.create-platform-application-unknown-platform-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // BAIDU accepts an arbitrary API-key/secret-key pair with no live credential validation.
        var okName = FidelityContext.UniqueName("sim2real-behav-sns-app-ok-");
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = okName,
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = FidelityContext.UniqueName("baidu-api-key-"),
                ["PlatformCredential"] = FidelityContext.UniqueName("baidu-secret-key-"),
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));
        Ensure((await AppNames(sns)).Contains(okName),
            $"positive control did not register {Py.Repr(okName)} in ListPlatformApplications");

        var badName = FidelityContext.UniqueName("sim2real-behav-sns-app-bad-");
        var (code, message) = await Error(() => sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = badName,
            Platform = "NOT_A_REAL_PLATFORM",
            Attributes = new() { ["PlatformCredential"] = "whatever" },
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got {Py.Repr(code)}");
        Ensure(message == "Invalid parameter: Platform Reason: NOT_A_REAL_PLATFORM is not supported",
            $"unexpected InvalidParameter message for an unknown Platform: {Py.Repr(message)}");

        Ensure(!(await AppNames(sns)).Contains(badName),
            $"CreatePlatformApplication was rejected but {Py.Repr(badName)} appears in " +
            "ListPlatformApplications anyway");
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected rejection, but the call SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }

    private static async Task<HashSet<string>> AppNames(AmazonSimpleNotificationServiceClient sns)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await sns.ListPlatformApplicationsAsync(new ListPlatformApplicationsRequest { NextToken = token });
            foreach (var app in page.PlatformApplications ?? [])
            {
                names.Add(app.PlatformApplicationArn[(app.PlatformApplicationArn.LastIndexOf('/') + 1)..]);
            }

            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return names;
    }
}
