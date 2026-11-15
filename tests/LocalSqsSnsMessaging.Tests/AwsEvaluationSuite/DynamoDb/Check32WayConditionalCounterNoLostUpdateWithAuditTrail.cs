// Translated from behavioral/dynamodb/check_32_way_conditional_counter_no_lost_update_with_audit_trail.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.32-way-conditional-counter-no-lost-update-with-audit-trail</summary>
[Category("AwsEvaluationSuite")]
public sealed class Check32WayConditionalCounterNoLostUpdateWithAuditTrail
{
    private const string CheckId = "32-way-conditional-counter-no-lost-update-with-audit-trail";
    private const int Racers = 32;
    private const int MaxAttempts = 200;
    private const string ErrCcf = "ConditionalCheckFailedException";
    private const string MsgCcf = "The conditional request failed";

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("COUNTER#1")));

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 32 concurrent read-then-conditional-UpdateItem increments leave the counter at 32 and an
    /// audit list whose before/after values form the unbroken chain 0->32 with no value claimed
    /// twice; any refused attempt is ConditionalCheckFailedException with its exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.32-way-conditional-counter-no-lost-update-with-audit-trail")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.DynamoClient(maxAttempts: 5);
        var table = FidelityContext.UniqueName("sim2real-behav-d1-cond-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("COUNTER#1")), ("n", N("0"))));
        var seeded = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(seeded["n"].N == "0", $"the seeded counter reads {Py.Repr(seeded["n"].N)}, expected \"0\"");
        Ensure(!seeded.ContainsKey("audit"), $"the seeded item already carries an audit list: {Py.Repr(seeded)}");

        async Task<(int Worker, int Rejections, string? Error)> Racer(int worker)
        {
            var rejections = 0;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
                var cur = int.Parse(item["n"].N, CultureInfo.InvariantCulture);
                var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Key(),
                    UpdateExpression = "SET #n = :next, #a = list_append(if_not_exists(#a, :empty), :entry)",
                    ConditionExpression = "#n = :expected",
                    ExpressionAttributeNames = Names(("#n", "n"), ("#a", "audit")),
                    ExpressionAttributeValues = Item(
                        (":next", N(Str(cur + 1))),
                        (":expected", N(Str(cur))),
                        (":empty", L()),
                        (":entry", L(M(("worker", N(Str(worker))), ("before", N(Str(cur))), ("after", N(Str(cur + 1))))))),
                }));
                if (error is null)
                {
                    return (worker, rejections, null);
                }

                if ((error.Code, error.Message) != (ErrCcf, MsgCcf))
                {
                    return (worker, rejections, $"worker {worker}: {error.Code}: {Py.Repr(error.Message)}");
                }

                rejections++;
            }

            return (worker, rejections, $"worker {worker} exhausted its {MaxAttempts} attempt budget after {rejections} ConditionalCheckFailedException(s)");
        }

        (int Worker, int Rejections, string? Error)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(w => Task.Run(() => Racer(w), cancellationToken)));
        }

        var errors = results.Where(r => r.Error is not null).Select(r => r.Error).ToList();
        Ensure(errors.Count == 0, $"a racer failed to land its increment: {Py.Repr(errors)}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            final["n"].N == Str(Racers),
            $"lost update: the counter reads {Py.Repr(final["n"].N)} after {Racers} conditional increments that each reported success, expected {Py.Repr(Str(Racers))}");

        var audit = final["audit"].L;
        Ensure(
            audit.Count == Racers,
            $"the audit trail written by the same UpdateItem calls holds {audit.Count} entries, expected {Racers} -- the counter reached {Racers} without {Racers} applied increments");
        var entries = audit
            .Select(e => (
                Worker: int.Parse(e.M["worker"].N, CultureInfo.InvariantCulture),
                Before: int.Parse(e.M["before"].N, CultureInfo.InvariantCulture),
                After: int.Parse(e.M["after"].N, CultureInfo.InvariantCulture)))
            .ToList();

        var workers = entries.Select(e => e.Worker).Order().ToList();
        Ensure(
            workers.SequenceEqual(Enumerable.Range(0, Racers)),
            $"the audit workers are {Py.Repr(workers)}; expected each of 0..{Racers - 1} exactly once");
        var befores = entries.Select(e => e.Before).Order().ToList();
        Ensure(
            befores.SequenceEqual(Enumerable.Range(0, Racers)),
            $"two racers recorded the same predecessor value -- this is a LOST UPDATE. The `before` values were {Py.Repr(befores)}, expected each of 0..{Racers - 1} exactly once");
        var afters = entries.Select(e => e.After).Order().ToList();
        Ensure(
            afters.SequenceEqual(Enumerable.Range(1, Racers)),
            $"the audit `after` values are {Py.Repr(afters)}, expected 1..{Racers}");
        foreach (var (worker, before, after) in entries)
        {
            Ensure(
                after == before + 1,
                $"worker {worker} recorded before={before} after={after}; an increment must advance the counter by exactly one");
        }

        var chain = entries.OrderBy(e => e.Before).ToList();
        for (var step = 0; step < chain.Count; step++)
        {
            var (worker, before, after) = chain[step];
            Ensure(
                (before, after) == (step, step + 1),
                $"the audit trail is not an unbroken chain: sorted position {step} is worker {worker} with before={before} after={after}, expected {step}->{step + 1}");
        }
    }
}
