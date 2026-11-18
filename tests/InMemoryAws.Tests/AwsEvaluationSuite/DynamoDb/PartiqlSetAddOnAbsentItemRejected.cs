// Translated from behavioral/dynamodb/check_partiql_set_add_on_absent_item_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-add-on-absent-item-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetAddOnAbsentItemRejected
{
    private const string CheckId = "partiql-set-add-on-absent-item-rejected";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>The (Code, Message) <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected Code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected Message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var budgetS = Math.Max(5.0, Math.Min(90.0, ctx.TimeLeftSeconds - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {Py.Repr(budgetS)}s: {last}");
    }

    /// <summary>
    /// UPDATE ... SET tags = set_add(...) against a primary key with no item raises
    /// ConditionalCheckFailedException and creates nothing; the identical statement against an
    /// existing item still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-add-on-absent-item-rejected")]
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

        var statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'x'>>) WHERE pk = ?";
        var missingKey = Item(("pk", S("no-such-item")));

        // Break exactly one thing: the target key has no item.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [missingKey["pk"]] }),
            Ccf);
        var created = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = missingKey, ConsistentRead = true })).Item;
        Ensure(created is null, $"set_add against an absent item must create nothing, got {Py.Repr(created)}");

        // Positive control: the same statement against an item that exists succeeds.
        var existingKey = Item(("pk", S("has-item")));
        await ddb.PutItemAsync(t, new Dictionary<string, AttributeValue>(existingKey, StringComparer.Ordinal));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [existingKey["pk"]] });
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = existingKey, ConsistentRead = true })).Item;
        Ensure(Py.Equal(item.GetValueOrDefault("tags"), SS("x")), $"the positive control's set_add must store {{'SS': ['x']}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");
    }
}
