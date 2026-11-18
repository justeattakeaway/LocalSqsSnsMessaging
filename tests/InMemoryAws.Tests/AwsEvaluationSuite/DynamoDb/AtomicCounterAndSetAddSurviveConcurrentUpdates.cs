// Translated from behavioral/dynamodb/check_atomic_counter_and_set_add_survive_concurrent_updates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.atomic-counter-and-set-add-survive-concurrent-updates</summary>
[Category("AwsEvaluationSuite")]
public sealed class AtomicCounterAndSetAddSurviveConcurrentUpdates
{
    private const string Check = "atomic-counter-and-set-add-survive-concurrent-updates";
    private const int Workers = 10;

    /// <summary>
    /// Ten concurrent UpdateItem `ADD` calls on one item leave the counter at exactly 10 and the
    /// string set holding the ten distinct tokens: ADD is atomic, so no update is lost. Both are
    /// count invariants that hold under every interleaving.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.atomic-counter-and-set-add-survive-concurrent-updates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-atomicadd-");
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

        var key = Item(("pk", S("COUNTER#1")));
        await ddb.PutItemAsync(table, Item(("pk", S("COUNTER#1")), ("counter", N("0"))));

        var expectedTokens = Enumerable.Range(0, Workers).Select(i => $"worker-{i}").ToHashSet(StringComparer.Ordinal);

        async Task<string?> Bump(int i)
        {
            try
            {
                await ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = key,
                    UpdateExpression = "ADD #c :one, #m :tok",
                    ExpressionAttributeNames = Names(("#c", "counter"), ("#m", "members")),
                    ExpressionAttributeValues = Item((":one", N("1")), (":tok", SS($"worker-{i}"))),
                });
                return null;
            }
#pragma warning disable CA1031 // reported, never swallowed
            catch (Exception exc)
#pragma warning restore CA1031
            {
                return $"worker-{i}: {exc.GetType().Name}: {exc.Message}";
            }
        }

        List<string?> errors;
        using (ctx.Concurrently())
        {
            errors = (await Task.WhenAll(Enumerable.Range(0, Workers).Select(i => Task.Run(() => Bump(i), cancellationToken))))
                .Where(e => e is not null).ToList();
        }

        Ensure(errors.Count == 0, $"a concurrent ADD failed outright: {Py.Repr(errors)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var workersText = Workers.ToString(CultureInfo.InvariantCulture);
        Ensure(
            item["counter"].N == workersText,
            $"lost update on the atomic counter: got {Py.Repr(item["counter"].N)}, expected " +
            $"{Py.Repr(workersText)} after {Py.Repr(Workers)} concurrent ADDs");
        var members = item["members"].SS.ToHashSet(StringComparer.Ordinal);
        Ensure(
            members.SetEquals(expectedTokens),
            $"the string set lost members under concurrency: {Py.Repr(expectedTokens.Except(members).Order(StringComparer.Ordinal).ToList())} " +
            $"missing, {Py.Repr(members.Except(expectedTokens).Order(StringComparer.Ordinal).ToList())} unexpected");
        Ensure(
            item["members"].SS.Count == Workers,
            $"the set has the wrong cardinality: {Py.Repr(item["members"].SS.Count)} entries in " +
            $"{Py.Repr(item["members"].SS)}");
    }
}
