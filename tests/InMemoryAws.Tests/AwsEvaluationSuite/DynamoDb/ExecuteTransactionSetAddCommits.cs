// Translated from behavioral/dynamodb/check_execute_transaction_set_add_commits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-set-add-commits</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionSetAddCommits
{
    private const string CheckId = "execute-transaction-set-add-commits";

    /// <summary>
    /// A set_add update sent inside ExecuteTransaction commits atomically alongside a sibling
    /// statement, and a consistent read sees both writes afterward.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteTransaction.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-set-add-commits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ctx, ddb, t);

        var key1 = Item(("pk", S("s1")));
        var key2 = Item(("pk", S("s2")));
        await ddb.PutItemAsync(t, new Dictionary<string, AttributeValue>(key1));
        await ddb.PutItemAsync(t, new Dictionary<string, AttributeValue>(key2));

        var resp = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'v'>>) WHERE pk = ?", Parameters = [key1["pk"]] },
                new ParameterizedStatement { Statement = $"UPDATE \"{t}\" SET note = ? WHERE pk = ?", Parameters = [S("committed"), key2["pk"]] },
            ],
        });
        Ensure(resp.Responses is { Count: 0 }, $"ExecuteTransaction with no RETURNING clause returns no Responses, got {Py.Repr(resp.Responses)}");

        var item1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key1, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item1.GetValueOrDefault("tags"), SS("v")),
            $"the transaction's set_add must store {{'SS': ['v']}}, got {Py.Repr(item1.GetValueOrDefault("tags"))}");
        var item2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key2, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item2.GetValueOrDefault("note"), S("committed")),
            $"the sibling statement in the same transaction must also have committed, got {Py.Repr(item2.GetValueOrDefault("note"))}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var left = ctx.TimeLeftSeconds;
        var budgetSeconds = Math.Max(5.0, Math.Min(90.0, left - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(budgetSeconds))
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetSeconds.ToString(CultureInfo.InvariantCulture)}s: {last?.TableStatus}");
    }
}
