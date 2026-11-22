// Translated from behavioral/dynamodb/check_concurrent_add_put_interleave_final_counter_matches_last_add.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-add-put-interleave-final-counter-matches-last-add</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentAddPutInterleaveFinalCounterMatchesLastAdd
{
    private const string Check = "concurrent-add-put-interleave-final-counter-matches-last-add";
    private const int NumAdds = 10;
    private const int NumPuts = 10;

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("k")));

    /// <summary>
    /// Interleaved full-item PutItem resets and ADD increments on one key leave a final item
    /// with exactly {pk,a,b}; a is either 0 (the last applied write was the Put) or equal to the
    /// UPDATED_NEW some ADD call actually returned, matching DynamoDB's per-item serialisation.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-add-put-interleave-final-counter-matches-last-add")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-addput-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("k")), ("a", N("0")), ("b", S("init"))));

        async Task<(string Kind, int? Value)> Add()
        {
            var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "ADD a :one",
                ExpressionAttributeValues = Item((":one", N("1"))),
                ReturnValues = ReturnValue.UPDATED_NEW,
            });
            return ("add", int.Parse(resp.Attributes["a"].N, CultureInfo.InvariantCulture));
        }

        async Task<(string Kind, int? Value)> Put()
        {
            await ddb.PutItemAsync(table, Item(("pk", S("k")), ("a", N("0")), ("b", S("put"))));
            return ("put", null);
        }

        var fns = Enumerable.Range(0, NumAdds + NumPuts).Select(i => i % 2 == 0 ? (Func<Task<(string, int?)>>)Add : Put).ToList();
        (string Kind, int? Value)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(fns.Select(fn => Task.Run(fn)));
        }

        var addValues = results.Where(r => r.Kind == "add").Select(r => r.Value!.Value).ToList();
        Ensure(addValues.Count == NumAdds, $"expected {NumAdds} ADD results, got {addValues.Count}: {Py.Repr(addValues)}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            final.Keys.ToHashSet().SetEquals(["pk", "a", "b"]),
            $"final item carries {Py.Repr(final.Keys.Order(StringComparer.Ordinal))}, expected exactly pk, a and b");
        var bVal = final["b"].S;
        Ensure(bVal is "init" or "put", $"final b was {Py.Repr(bVal)}, expected 'init' or 'put'");
        var aVal = int.Parse(final["a"].N, CultureInfo.InvariantCulture);
        Ensure(aVal is >= 0 and <= NumAdds, $"final a={aVal} is outside the possible range 0..{NumAdds}");

        if (aVal == 0)
        {
            Ensure(bVal == "put", $"final a=0 can only follow a Put reset, but b={Py.Repr(bVal)}, expected 'put'");
        }
        else
        {
            Ensure(
                addValues.Contains(aVal),
                $"final a={aVal} matches none of the UPDATED_NEW values the ADD calls returned "
                + $"({Py.Repr(addValues.Order())}); the stored counter must equal the value the last applied "
                + "ADD actually returned");
        }
    }
}
