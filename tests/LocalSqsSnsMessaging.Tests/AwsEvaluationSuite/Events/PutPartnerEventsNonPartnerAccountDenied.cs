// Translated from behavioral/events/check_put_partner_events_non_partner_account_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-partner-events-non-partner-account-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutPartnerEventsNonPartnerAccountDenied
{
    /// <summary>
    /// PutPartnerEvents from a non-partner account fails the well-formed partner entry per entry
    /// (ErrorCode, no EventId), creates no event source, and PutEvents to the default bus still works.
    /// </summary>
    [Test]
    [DisplayName("events.put-partner-events-non-partner-account-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var partnerSegment = FidelityContext.UniqueName("sim2real-behav-partner-");
        var sourceName = $"aws.partner/{partnerSegment}/testnamespace/testevent";

        var putRes = await events.PutPartnerEventsAsync(new PutPartnerEventsRequest
        {
            Entries =
            [
                new PutPartnerEventsRequestEntry
                {
                    Source = sourceName,
                    DetailType = "sim2real-test",
                    Detail = "{}"
                }
            ]
        });
        var entry = putRes.Entries[0];
        var diag = $"failed={putRes.FailedEntryCount} entry={{'EventId': '{entry.EventId}', 'ErrorCode': '{entry.ErrorCode}', 'ErrorMessage': '{entry.ErrorMessage}'}}";
        Ensure(putRes.FailedEntryCount == 1, diag);
        Ensure(entry.EventId is null, diag);
        Ensure(!string.IsNullOrEmpty(entry.ErrorCode), diag);

        // Effect absent: the rejected call must not have caused this name to become a describable
        // partner event source shared with the account.
        var describeErr = await ClientError(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
        Ensure(
            describeErr.Code == "ResourceNotFoundException",
            $"expected the never-created partner source to 404, got {describeErr}");
        var expectedMessage = $"Event source {sourceName} does not exist.";
        Ensure(
            describeErr.Message == expectedMessage,
            $"expected message '{expectedMessage}', got '{describeErr.Message}'");

        // Positive control: the exact same credentials can still PutEvents to the account's own
        // default bus -- this is a targeted partner-authorization rejection, not a broken client or
        // a blanket deny.
        var putResult = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "sim2real.behavioral.control",
                    DetailType = "sim2real-control",
                    Detail = "{}"
                }
            ]
        });
        var controlEntry = putResult.Entries[0];
        Ensure(
            putResult.FailedEntryCount == 0,
            "expected the positive control PutEvents to the account's own default bus to succeed " +
            $"with the same credentials, got FailedEntryCount={putResult.FailedEntryCount} " +
            $"entry={{'ErrorCode': '{controlEntry.ErrorCode}', 'ErrorMessage': '{controlEntry.ErrorMessage}'}}");
        Ensure(
            controlEntry.EventId is not null,
            "expected the positive control entry to carry an EventId, got entry=" +
            $"{{'ErrorCode': '{controlEntry.ErrorCode}', 'ErrorMessage': '{controlEntry.ErrorMessage}'}}");
    }

    /// <summary>Botocore Error dict of a call that must fail; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error ?? throw Fail("expected this call to be rejected, but it SUCCEEDED");
    }
}
