// Translated from behavioral/events/check_e2e_partner_event_source_activation_to_ingestion_flow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.e2e-partner-event-source-activation-to-ingestion-flow</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2EPartnerEventSourceActivationToIngestionFlow
{
    /// <summary>
    /// A non-partner account's CreatePartnerEventSource is rejected with ValidationException, the
    /// source then describes as not found, and PutPartnerEvents through it fails per entry with
    /// InvalidArgument, while the same credentials create a custom bus and deliver PutEvents to it.
    /// </summary>
    [Test]
    [DisplayName("events.e2e-partner-event-source-activation-to-ingestion-flow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var accountId = ctx.Account;

        var partner = FidelityContext.UniqueName("sim2real-partner-").Replace('_', '-');
        var generator = FidelityContext.UniqueName("src-");
        var sourceName = $"aws.partner/{partner}/{generator}";

        bool created;
        var createError = await FidelityContext.CaptureError(() =>
            events.CreatePartnerEventSourceAsync(new CreatePartnerEventSourceRequest { Name = sourceName, Account = accountId }));
        if (createError is null)
        {
            ctx.OnCleanup(() => IgnoreNotFound(() =>
                events.DeletePartnerEventSourceAsync(new DeletePartnerEventSourceRequest { Name = sourceName, Account = accountId })));
            created = true;
        }
        else
        {
            created = false;
            (string?, string?) createErr = (createError.Code, createError.Message);
            Ensure(
                createErr == ("ValidationException", NotAllowlisted(sourceName)),
                $"expected ValidationException for a non-allowlisted partner namespace, got {createErr}");
        }

        Ensure(
            !created,
            "CreatePartnerEventSource unexpectedly succeeded for a non-whitelisted caller account -- " +
            "this account has no partner rights to this namespace");

        // Because creation was rejected, the event source must not exist on the customer side
        // either -- no activation/ingestion state should have leaked into existence.
        var err = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
        Ensure(
            err == ("ResourceNotFoundException", $"Event source {sourceName} does not exist."),
            $"expected no event source to exist after a rejected CreatePartnerEventSource, got {err}");

        // And PutPartnerEvents through the never-created source must fail per-entry rather than
        // being accepted as if the source existed.
        var resp = await PutPartnerEvent(events, sourceName);
        Ensure(resp.FailedEntryCount == 1, "assert resp[\"FailedEntryCount\"] == 1");
        var entry = resp.Entries[0];
        Ensure(entry.EventId is null, "assert \"EventId\" not in entry");
        Ensure(
            (entry.ErrorCode, entry.ErrorMessage) == (
                "InvalidArgument",
                "Parameter Source is not valid. Reason: Caller AWS account is not whitelisted for the source."),
            $"{{'ErrorCode': '{entry.ErrorCode}', 'ErrorMessage': '{entry.ErrorMessage}'}}");

        // Positive control: the exact same credentials can still create, describe and deliver to an
        // ordinary (non-partner) bus -- proving the rejections above are a targeted partner-namespace
        // boundary, not a broken client or a blanket deny that would make every assertion above vacuous.
        var busName = FidelityContext.UniqueName("sim2real-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(described.Name == busName, $"{{'Name': '{described.Name}'}}");
        var putResult = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "sim2real.control",
                    DetailType = "sim2real-control",
                    Detail = "{\"ok\": true}",
                    EventBusName = busName
                }
            ]
        });
        Ensure(putResult.FailedEntryCount == 0, $"FailedEntryCount={putResult.FailedEntryCount}");
        Ensure(putResult.Entries[0].EventId is not null, "expected an EventId in the PutEvents entry");
    }

    /// <summary>
    /// EventBridge's refusal for a caller outside a partner namespace; it names
    /// aws.partner/&lt;namespace&gt;, the first two segments of the source name or prefix.
    /// </summary>
    private static string NotAllowlisted(string name)
    {
        var ns = string.Join("/", name.Split('/').Take(2));
        return $"Caller is not whitelisted for the event source partner namespace {ns}";
    }

    private static Task<PutPartnerEventsResponse> PutPartnerEvent(Amazon.EventBridge.AmazonEventBridgeClient events, string sourceName) =>
        events.PutPartnerEventsAsync(new PutPartnerEventsRequest
        {
            Entries =
            [
                new PutPartnerEventsRequestEntry
                {
                    Time = FidelityTime.UtcNow.UtcDateTime,
                    Source = sourceName,
                    DetailType = "sim2real-partner-test",
                    Detail = "{\"ok\": true}"
                }
            ]
        });

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Run a teardown delete; only an already-gone resource is not an error.</summary>
    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
