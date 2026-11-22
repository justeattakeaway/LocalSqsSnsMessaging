// Translated from behavioral/dynamodb/check_transact_update_if_not_exists_seeds_counter_and_list_on_absent_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-update-if-not-exists-seeds-counter-and-list-on-absent-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUpdateIfNotExistsSeedsCounterAndListOnAbsentItem
{
    private const string Check = "transact-update-if-not-exists-seeds-counter-and-list-on-absent-item";

    /// <summary>
    /// A TransactWriteItems Update on an absent key creates the item, if_not_exists seeding a counter
    /// and a list_append target, and a second transaction applies the same expression to stored values.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-update-if-not-exists-seeds-counter-and-list-on-absent-item")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        const string expression = "SET c = if_not_exists(c, :zero) + :one, h = list_append(if_not_exists(h, :empty), :tail)";

        List<TransactWriteItem> Items(string tail, string marker) =>
        [
            new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S(marker))) } },
            new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = t,
                    Key = Item(("pk", S("doc"))),
                    UpdateExpression = expression,
                    ExpressionAttributeValues = Item(
                        (":zero", N("0")), (":one", N("1")), (":empty", L()),
                        (":tail", L(S(tail)))),
                },
            },
        ];

        var absent = await Get(ddb, t, "doc");
        Ensure(absent is null, $"the fresh table already held the item: {Py.Repr(absent)}");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Items("a", "marker-1") });
        var doc = await Get(ddb, t, "doc");
        Ensure(
            Py.Equal(doc, Item(("pk", S("doc")), ("c", N("1")), ("h", L(S("a"))))),
            $"the first transaction created {Py.Repr(doc)}");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Items("b", "marker-2") });
        doc = await Get(ddb, t, "doc");
        Ensure(
            Py.Equal(doc, Item(("pk", S("doc")), ("c", N("2")), ("h", L(S("a"), S("b"))))),
            $"the second transaction left {Py.Repr(doc)}");
        foreach (var marker in new[] { "marker-1", "marker-2" })
        {
            var got = await Get(ddb, t, marker);
            Ensure(Py.Equal(got, Item(("pk", S(marker)))), $"the Put committed beside the Update read {Py.Repr(got)}");
        }
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
