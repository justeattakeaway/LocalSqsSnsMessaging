// Translated from behavioral/dynamodb/check_concurrent_put_item_last_writer_wins_without_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-put-item-last-writer-wins-without-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentPutItemLastWriterWinsWithoutCondition
{
    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>
    /// Ten concurrent unconditional PutItem calls to one key all succeed, and consistent reads
    /// afterwards keep returning the same single writer's value, never flapping between candidates.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-put-item-last-writer-wins-without-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await SimpleTable(ddb, ctx);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("lww-1")));
        const int n = 10;
        var values = Enumerable.Range(0, n).Select(i => $"writer-{i}").ToList();

        Func<Task<PutItemResponse>> MakeWriter(string v) => () => ddb.PutItemAsync(name, Item(("pk", S("lww-1")), ("writer", S(v))));

        object[] results;
        using (ctx.Concurrently())
        {
            results = await RunConcurrent([.. values.Select(MakeWriter)]);
        }

        var successes = ConcurrentSuccesses(results);
        Ensure(successes == n, $"unconditional PutItem calls must all succeed, got {successes}/{n}: {Py.Repr(results.Select(r => r is Exception ex ? ex.Message : "ok"))}");
        var codes = ConcurrentErrorCodes(results);
        Ensure(codes.Count == 0, $"unconditional PutItem must never fail with a conditional error: {Py.Repr(codes)}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
        Ensure(values.Contains(final["writer"].S), Py.Repr(final));

        for (var i = 0; i < 3; i++)
        {
            var again = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
            Ensure(again["writer"].S == final["writer"].S, Py.Repr((again, final)));
        }
    }

    /// <summary>Poll DescribeTable until the table AND every GSI on it report ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 90.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (last.TableStatus == TableStatus.ACTIVE && (last.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE"))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>Create a single-"pk" (S) hash-key table, register teardown, wait ACTIVE, return the name.</summary>
    private static async Task<string> SimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx, string prefix = "sim2real-behav-ddb-tbl-")
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    /// <summary>Runs every fn concurrently; each result is its response or the exception it raised.</summary>
    private static async Task<object[]> RunConcurrent<T>(IReadOnlyList<Func<Task<T>>> fns)
        where T : class =>
        await Task.WhenAll(fns.Select(fn => Task.Run<object>(async () =>
        {
            try
            {
                return await fn();
            }
#pragma warning disable CA1031 // as the Python check, every failure is recorded as a result
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        })));

    private static int ConcurrentSuccesses(object[] results) => results.Count(r => r is not Exception);

    private static List<string?> ConcurrentErrorCodes(object[] results) =>
    [
        .. results.OfType<Exception>().Select(r => r is Amazon.Runtime.AmazonServiceException ex ? AwsError.From(ex).Code : r.GetType().Name),
    ];
}
