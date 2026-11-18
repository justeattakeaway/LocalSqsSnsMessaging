// Translated from behavioral/dynamodb/check_oversized_item_to_a_missing_table_not_found_on_put_rejected_on_batch_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.oversized-item-to-a-missing-table-not-found-on-put-rejected-on-batch-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class OversizedItemToAMissingTableNotFoundOnPutRejectedOnBatchWrite
{
    private const int ItemLimitBytes = 400 * 1024;
    private static readonly (string, string) NotFound = ("ResourceNotFoundException", "Requested resource not found");
    private static readonly (string, string) TooBig = ("ValidationException", "Item size has exceeded the maximum allowed size");

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// An item over 400 KB sent by PutItem to a missing table is ResourceNotFound, by BatchWriteItem
    /// the item-size ValidationException; to an existing table both refuse it by size and store nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.oversized-item-to-a-missing-table-not-found-on-put-rejected-on-batch-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-bigitem-");
        var missing = $"{table}-absent";
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> Big() => Item(("pk", S("big")), ("blob", S(new string('x', ItemLimitBytes))));

        async Task<object> Put(string name) => await ddb.PutItemAsync(name, Big());

        async Task<object> Batch(string name) => await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [name] = [new WriteRequest { PutRequest = new PutRequest { Item = Big() } }] },
        });

        // PutItem looks the table up before sizing the item; BatchWriteItem sizes it first.
        foreach (var (label, fn, want) in new (string, Func<string, Task<object>>, (string, string))[] { ("PutItem", Put, NotFound), ("BatchWriteItem", Batch, TooBig) })
        {
            var (code, msg) = await Error(() => fn(missing));
            Ensure((code, msg) == want, $"{label} of an oversized item to the missing table gave {Py.Repr(code)}: {Py.Repr(msg)}");
            (code, msg) = await Error(() => fn(table));
            Ensure((code, msg) == TooBig, $"{label} of an oversized item to the existing table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("big"))), ConsistentRead = true });
        Ensure(gone.Item is null, $"the oversized item was stored: {Py.Repr(gone.Item?.Keys.Order(StringComparer.Ordinal).ToList())}");
        await ddb.PutItemAsync(table, Item(("pk", S("small")), ("blob", S("x"))));
        var small = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("small"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(small, Item(("pk", S("small")), ("blob", S("x")))), $"the small control item reads back {Py.Repr(small)}");
        var (describeCode, describeMsg) = await Error(async () => await ddb.DescribeTableAsync(missing));
        var wantDescribe = ("ResourceNotFoundException", $"Requested resource not found: Table: {missing} not found");
        Ensure((describeCode, describeMsg) == wantDescribe, $"the missing table describes as {Py.Repr(describeCode)}: {Py.Repr(describeMsg)}");
    }
}
