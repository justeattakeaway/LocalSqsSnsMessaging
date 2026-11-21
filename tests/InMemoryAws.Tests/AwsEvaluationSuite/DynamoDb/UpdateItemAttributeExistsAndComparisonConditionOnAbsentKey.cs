// Translated from behavioral/dynamodb/check_update_item_attribute_exists_and_comparison_condition_on_absent_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-attribute-exists-and-comparison-condition-on-absent-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAttributeExistsAndComparisonConditionOnAbsentKey
{
    private const string Check = "update-item-attribute-exists-and-comparison-condition-on-absent-key";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Call fn() and grade the exact (code, message) it raises; a success is itself a failure.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected {Py.Repr(expected.Code)} but the call succeeded: {result}");
        }

        Ensure(err.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = 60.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS:0}s; last status {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// A standalone UpdateItem whose ConditionExpression ANDs attribute_exists(pk) with an
    /// attribute-value comparison fails with ConditionalCheckFailedException on an absent key and
    /// creates no item; the same call succeeds once the item and the compared attribute both exist.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-attribute-exists-and-comparison-condition-on-absent-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitTableActive(ddb, table);

        var absentKey = Item(("pk", S("absent-order")));

        async Task<object> Restock() => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = absentKey,
            UpdateExpression = "SET stock = :s",
            ConditionExpression = "attribute_exists(pk) AND #st = :want",
            ExpressionAttributeNames = Names(("#st", "status")),
            ExpressionAttributeValues = Item((":s", N("5")), (":want", S("open"))),
        });

        await ExpectError(Restock, Ccf);

        var ghost = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = absentKey, ConsistentRead = true })).Item;
        Ensure(ghost is null, $"the rejected update must create no ghost item, got {Py.Repr(ghost)}");

        // Positive control: create the item with a matching status, then the identical call commits.
        await ddb.PutItemAsync(table, Item(("pk", S("absent-order")), ("status", S("open"))));
        await Restock();
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = absentKey, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(after, Item(("pk", S("absent-order")), ("status", S("open")), ("stock", N("5")))),
            $"expected the restored conditional update to land, got {Py.Repr(after)}");
    }
}
