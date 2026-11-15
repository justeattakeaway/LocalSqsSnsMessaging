// Translated from behavioral/dynamodb/check_execute_statement_conditional_update_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-conditional-update-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementConditionalUpdateFailure
{
    /// <summary>
    /// Concurrent PartiQL `UPDATE ... WHERE pk = ? AND counter = 0` statements yield exactly one winner; every other
    /// raises ConditionalCheckFailedException ("The conditional request failed") and the counter ends at 1.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-conditional-update-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await SimpleTable(ddb, ctx);
        const string pk = "pql-cond-1";
        await ddb.PutItemAsync(name, Item(("pk", S(pk)), ("counter", N("0"))));

        Task<(ExecuteStatementResponse? Response, AwsError? Error)> TryClaim() => FidelityContext.Capture(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{name}\" SET counter = ? WHERE pk = ? AND counter = ?",
            Parameters = [N("1"), S(pk), N("0")],
        }));

        const int n = 8;
        (ExecuteStatementResponse? Response, AwsError? Error)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(TryClaim, cancellationToken)));
        }

        var successes = results.Count(r => r.Error is null);
        Ensure(successes == 1, $"expected exactly one PartiQL UPDATE to win the race, got {successes}: {Py.Repr(results.Select(r => r.Error?.ToString() ?? "ok").ToList())}");

        var codes = results.Where(r => r.Error is not null).Select(r => r.Error!.Code).ToList();
        Ensure(codes.Count == n - 1, $"expected {n - 1} losing racers, got {codes.Count}: {Py.Repr(codes)}");
        Ensure(
            codes.All(c => c == "ConditionalCheckFailedException"),
            $"expected every losing racer to get ConditionalCheckFailedException, got {Py.Repr(codes)}");
        var messages = results.Where(r => r.Error is not null).Select(r => r.Error!.Message).ToList();
        Ensure(
            messages.SequenceEqual(Enumerable.Repeat("The conditional request failed", n - 1)),
            $"expected every losing racer to get the message 'The conditional request failed', got {Py.Repr(messages)}");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(final["counter"], N("1")), $"exactly one PartiQL UPDATE must have applied, got counter={Py.Repr(final["counter"])}");
    }

    /// <summary>Poll DescribeTable until the table AND every GSI on it report ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE && (last.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE"))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s: {last?.TableStatus}");
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
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }
}
