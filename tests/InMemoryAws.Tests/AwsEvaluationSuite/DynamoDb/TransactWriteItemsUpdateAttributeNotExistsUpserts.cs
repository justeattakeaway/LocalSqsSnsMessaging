// Translated from behavioral/dynamodb/check_transact_write_items_update_attribute_not_exists_upserts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-attribute-not-exists-upserts</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateAttributeNotExistsUpserts
{
    private const string Canceled = "TransactionCanceledException";
    private const string CcfMessage = "The conditional request failed";

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is null || notFound.Contains(error.Code))
        {
            return;
        }

        throw error.Exception;
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableStatus? status = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(status)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>
    /// A transact Update conditioned on attribute_not_exists upserts an absent item with its SET
    /// attributes and key; once the item exists, replaying the same transaction cancels instead of
    /// overwriting it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-attribute-not-exists-upserts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-upsert-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var upsertKey = Item(("pk", S("new-item")));
        var siblingKey = Item(("pk", S("sibling-ok")));

        Task<TransactWriteItemsResponse> Upsert(string value) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = upsertKey,
                            UpdateExpression = "SET v = :v, note = :n",
                            ConditionExpression = "attribute_not_exists(pk)",
                            ExpressionAttributeValues = Item((":v", S(value)), (":n", S("via-upsert"))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Put = new Put { TableName = table, Item = Item(("pk", siblingKey["pk"]), ("v", S("sibling"))) },
                    },
                ],
            });

        await Upsert("created");
        var created = await Get(ddb, table, upsertKey);
        Ensure(
            Py.Equal(created, Item(("pk", upsertKey["pk"]), ("v", S("created")), ("note", S("via-upsert")))),
            $"expected the upsert to create the item with its SET attributes and key, got {Py.Repr(created)}");
        var siblingGot = await Get(ddb, table, siblingKey);
        Ensure(
            Py.Equal(siblingGot, Item(("pk", siblingKey["pk"]), ("v", S("sibling")))),
            $"expected the sibling Put in the same transaction to land, got {Py.Repr(siblingGot)}");

        // Replaying the same upsert now finds pk present: attribute_not_exists is false and the
        // transaction cancels instead of overwriting the item.
        var (result, err) = await FidelityContext.Capture(() => Upsert("overwritten"));
        if (err is not null)
        {
            Ensure(err.Code == Canceled, $"expected {Canceled}, got {err}");
            Ensure(
                err.Message == "Transaction cancelled, please refer cancellation reasons for specific reasons "
                    + "[ConditionalCheckFailed, None]",
                $"unexpected message {Py.Repr(err.Message)}");
            var reasons = (err.CancellationReasons() ?? []).Select(r => (r.Code, r.Message)).ToList();
            Ensure(
                reasons.Count == 2 && reasons[0] == ("ConditionalCheckFailed", CcfMessage) && reasons[1] == ("None", null),
                $"unexpected cancellation reasons {Py.Repr(reasons)}");
        }
        else
        {
            throw Fail($"replaying the upsert against an existing item was ACCEPTED: {result}");
        }

        var unchanged = await Get(ddb, table, upsertKey);
        Ensure(Py.Equal(unchanged, created), $"the cancelled replay must not overwrite the upserted item; got {Py.Repr(unchanged)}");
    }
}
