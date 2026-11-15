// Translated from behavioral/dynamodb/check_stale_condition_update_rejected_leaves_counter_and_audit_trail_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stale-condition-update-rejected-leaves-counter-and-audit-trail-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class StaleConditionUpdateRejectedLeavesCounterAndAuditTrailIntact
{
    private const int Racers = 16;
    private const int StaleRacers = 12;
    private const int MaxAttempts = 200;
    private const string ErrCcf = "ConditionalCheckFailedException";

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("COUNTER#1")));

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Call fn(); return the Error.Code if an error is raised.
    /// An unexpected success raises, so an accepted UpdateItem never reads as a code mismatch.
    /// </summary>
    private static async Task<string?> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("the guarded UpdateItem succeeded against a stale expected value");
        }

        return error.Code;
    }

    /// <summary>
    /// Concurrent UpdateItems with a stale condition all fail ConditionalCheckFailedException and
    /// neither the SET nor the list_append clause lands; the same call with the current value succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stale-condition-update-rejected-leaves-counter-and-audit-trail-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.DynamoClient(maxAttempts: 5);
        var table = FidelityContext.UniqueName("sim2real-behav-d1-stale-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "d1-stale-condition" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("COUNTER#1")), ("n", N("0"))));

        Task<UpdateItemResponse> Update(int worker, int expected, int nxt) => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #n = :next, #a = list_append(if_not_exists(#a, :empty), :entry)",
            ConditionExpression = "#n = :expected",
            ExpressionAttributeNames = Names(("#n", "n"), ("#a", "audit")),
            ExpressionAttributeValues = Item(
                (":next", N(Str(nxt))),
                (":expected", N(Str(expected))),
                (":empty", L()),
                (":entry", L(M(("worker", N(Str(worker))), ("before", N(Str(expected))), ("after", N(Str(nxt))))))),
        });

        async Task<Dictionary<string, AttributeValue>> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        // Racer arrival order is not guaranteed, so setup is asserted as a set and a count.
        async Task<string?> Racer(int worker)
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var cur = int.Parse((await Get())["n"].N, CultureInfo.InvariantCulture);
                var error = await FidelityContext.CaptureError(() => Update(worker, cur, cur + 1));
                if (error is null)
                {
                    return null;
                }

                if (error.Code != ErrCcf)
                {
                    return $"worker {worker}: {error.Code}";
                }
            }

            return $"worker {worker} exhausted its {MaxAttempts} attempt budget";
        }

        List<string> errors;
        using (ctx.Concurrently())
        {
            errors = [.. (await Task.WhenAll(Enumerable.Range(0, Racers).Select(w => Task.Run(() => Racer(w), cancellationToken))))
                .Where(e => e is not null)
                .Select(e => e!)];
        }

        Ensure(errors.Count == 0, $"the setup racers failed: {Py.Repr(errors)}");
        var item = await Get();
        Ensure(item["n"].N == Str(Racers), $"setup left the counter at {Py.Repr(item["n"].N)}, expected {Py.Repr(Str(Racers))}");
        var auditBefore = item["audit"].L;
        Ensure(auditBefore.Count == Racers, $"setup left {auditBefore.Count} audit entries, expected {Racers}");
        var workersBefore = auditBefore.Select(e => int.Parse(e.M["worker"].N, CultureInfo.InvariantCulture)).Order().ToList();
        Ensure(
            workersBefore.SequenceEqual(Enumerable.Range(0, Racers)),
            $"setup audit workers are {Py.Repr(workersBefore)}, expected 0..{Racers - 1} once each");

        // :expected = 0 was true at seed time and is now stale.
        string?[] codes;
        using (ctx.Concurrently())
        {
            codes = await Task.WhenAll(Enumerable.Range(0, StaleRacers)
                .Select(w => Task.Run(() => ErrorCode(() => Update(900 + w, 0, 1)), cancellationToken)));
        }

        Ensure(
            codes.All(c => c == ErrCcf),
            $"{StaleRacers} concurrent UpdateItem calls with a stale expected value returned {Py.Repr(codes)}, expected {Py.Repr(ErrCcf)} for every one");

        var after = await Get();
        Ensure(
            after["n"].N == Str(Racers),
            $"a rejected conditional UpdateItem still applied its SET clause: the counter reads {Py.Repr(after["n"].N)}, expected {Py.Repr(Str(Racers))}");
        var auditAfter = after["audit"].L;
        Ensure(
            auditAfter.Count == Racers,
            $"a rejected conditional UpdateItem still applied its list_append clause: the audit trail holds {auditAfter.Count} entries, expected {Racers}");
        var intruders = auditAfter
            .Select(e => int.Parse(e.M["worker"].N, CultureInfo.InvariantCulture))
            .Where(w => w >= 900)
            .Order()
            .ToList();
        Ensure(intruders.Count == 0, $"the audit trail carries entries from workers whose update was rejected: {Py.Repr(intruders)}");
        Ensure(
            Py.Equal(auditAfter, auditBefore),
            $"the rejected updates rewrote the audit trail: {auditBefore.Count} entries became {auditAfter.Count} with different content");

        await Update(999, Racers, Racers + 1);
        var final = await Get();
        Ensure(
            final["n"].N == Str(Racers + 1),
            $"the control UpdateItem with a current expected value left the counter at {Py.Repr(final["n"].N)}, expected {Py.Repr(Str(Racers + 1))} -- the twelve rejections above were not attributable to the condition");
        Ensure(
            final["audit"].L.Count == Racers + 1,
            $"the control UpdateItem left {final["audit"].L.Count} audit entries, expected {Racers + 1}");
        Ensure(
            int.Parse(final["audit"].L[^1].M["worker"].N, CultureInfo.InvariantCulture) == 999,
            $"the control entry is not the last appended: trail tail is {Py.Repr(final["audit"].L[^1])}");
    }
}
