// Translated from behavioral/dynamodb/check_transact_write_items_overlength_token_masks_other_violations_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-overlength-token-masks-other-violations-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsOverlengthTokenMasksOtherViolationsRejected
{
    private static async Task<AwsError> Error<T>(Func<Task<T>> call, string what)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it: {result}");
    }

    /// <summary>Poll DescribeTable to ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var limit = Math.Max(1.0, Math.Min(60.0, (timeLeft == 0 ? 60.0 : timeLeft) - 10.0));
        var clock = FidelityTime.StartNew();
        var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        while (status != "ACTIVE" && clock.Elapsed < TimeSpan.FromSeconds(limit))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        }

        Ensure(status == "ACTIVE", $"table did not reach ACTIVE in time, last status {Py.Repr(status)}");
    }

    /// <summary>
    /// A ClientRequestToken over 36 characters is the only violation TransactWriteItems reports
    /// even beside an action naming no operation and an invalid ReturnConsumedCapacity; drop the
    /// token defect and the other two are reported together instead.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-overlength-token-masks-other-violations-rejected")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ctx, ddb, table);

        // Positive control: a well-formed transaction is accepted and its write lands.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("control"))) } }],
        });
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(control is { Count: > 0 }, "the positive control item must exist after a well-formed TransactWriteItems");

        var longToken = new string('x', 37);
        TransactWriteItem NoOpAction() => new();
        TransactWriteItem ValidButBlockedPut() => new() { Put = new Put { TableName = table, Item = Item(("pk", S("should-not-write-1"))) } };

        // All three violations present: the overlength token is the only one reported.
        var err = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [NoOpAction(), ValidButBlockedPut()],
                ClientRequestToken = longToken,
                ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            }),
            "a TransactWriteItems with a no-op action, an overlength token and an invalid ReturnConsumedCapacity");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}");
        const string tokenOnlyMessage = "1 validation error detected: Value at 'ClientRequestToken' failed to "
            + "satisfy constraint: Member must have length less than or equal to 36";
        Ensure(err.Message == tokenOnlyMessage, $"expected {Py.Repr(tokenOnlyMessage)}, got {Py.Repr(err.Message)}");

        // Drop the token defect: the remaining two violations are now both reported, together.
        var err2 = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [NoOpAction(), ValidButBlockedPut()],
                ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            }),
            "a TransactWriteItems with a no-op action and an invalid ReturnConsumedCapacity, valid token");
        Ensure(err2.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err2.Code)}");
        const string twoViolationMessage =
            "2 validation errors detected: Value '' at 'transactItems' failed to satisfy constraint: "
            + "TransactWriteRequest should contain Delete or Put or Update or ConditionCheck; "
            + "Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy constraint: "
            + "Member must satisfy enum value set: [INDEXES, TOTAL, NONE]";
        Ensure(err2.Message == twoViolationMessage, $"expected {Py.Repr(twoViolationMessage)}, got {Py.Repr(err2.Message)}");

        var items = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items
            .Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            items.SetEquals(["control"]),
            $"neither rejected transaction may write anything; table holds {Py.Repr(items.Order(StringComparer.Ordinal).ToList())}");
    }
}
