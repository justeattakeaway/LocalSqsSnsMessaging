// Translated from behavioral/sns/check_list_platform_applications_invalid_next_token_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-platform-applications-invalid-next-token-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListPlatformApplicationsInvalidNextTokenRejected
{
    /// <summary>
    /// ListPlatformApplications with a malformed pagination token is rejected with
    /// InvalidParameter instead of returning an arbitrary page; an ordinary (token-less)
    /// call for the same account still succeeds, both before and after the bad call.
    /// </summary>
    [Test]
    [DisplayName("sns.list-platform-applications-invalid-next-token-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // Positive control: an ordinary (token-less) list call succeeds, whatever it returns.
        // (boto3 always has a PlatformApplications key; the .NET SDK leaves an empty list null, so
        // the call succeeding is what is checked.)
        var resp = await sns.ListPlatformApplicationsAsync(new ListPlatformApplicationsRequest());
        Ensure(resp is not null, "expected a PlatformApplications key in the response");

        // Break exactly one thing: a NextToken that could never have been issued by SNS.
        var (code, message) = await Error(() => sns.ListPlatformApplicationsAsync(new ListPlatformApplicationsRequest { NextToken = "not-a-real-token==" }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for a NextToken that was never issued, got {Py.Repr(code)}");
        const string expected = "Invalid parameter: NextToken Reason: token is invalid or expired";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Effect absent / no corruption: an ordinary list call still works afterwards.
        resp = await sns.ListPlatformApplicationsAsync(new ListPlatformApplicationsRequest());
        Ensure(resp is not null, "expected a PlatformApplications key in the response after the rejected call");
    }

    /// <summary>
    /// Call fn(); return its botocore Error (code, message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"real AWS rejects a NextToken it never issued, but the call SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}
