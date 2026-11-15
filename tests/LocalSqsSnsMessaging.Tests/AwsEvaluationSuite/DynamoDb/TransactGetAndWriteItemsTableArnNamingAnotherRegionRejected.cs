// Translated from behavioral/dynamodb/check_transact_get_and_write_items_table_arn_naming_another_region_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-and-write-items-table-arn-naming-another-region-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetAndWriteItemsTableArnNamingAnotherRegionRejected
{
    private const string CheckId = "transact-get-and-write-items-table-arn-naming-another-region-rejected";

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {result}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>The scenario's second region: us-east-1, unless the check already runs there.</summary>
    private static string OtherRegion(FidelityContext ctx) => ctx.Region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>
    /// TransactGetItems and TransactWriteItems refuse a TableName ARN naming a different Region with
    /// a ValidationException on that member's tableName path, and write nothing; the same ARN
    /// addressed from its own Region reads and writes normally.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-and-write-items-table-arn-naming-another-region-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var other = OtherRegion(ctx);
        var here = ctx.Dynamo;
        // region-pinned: the table ARN under test must name a Region other than the caller's.
        var there = ctx.DynamoClient(region: other);
        var table = FidelityContext.UniqueName("sim2real-behav-txnarn-");
        await there.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => there.DeleteTableAsync(table));
        await there.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);
        var arn = (await there.DescribeTableAsync(table)).Table.TableArn;
        Ensure(arn.Split(':')[3] == other, $"the {other} table's ARN is {Py.Repr(arn)}");

        Dictionary<string, AttributeValue> SeedKey() => Item(("pk", S("row-seed")));
        await there.PutItemAsync(table, Item(("pk", S("row-seed")), ("v", S("seed"))));

        await ExpectError(
            () => here.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems = [new TransactGetItem { Get = new Get { TableName = arn, Key = SeedKey() } }],
            }),
            "ValidationException",
            $"1 validation error detected: Value '{arn}' at 'transactItems.1.member.get.tableName' failed to satisfy constraint: Invalid AWS region");

        Dictionary<string, AttributeValue> NewKey() => Item(("pk", S("row-new")));
        await ExpectError(
            () => here.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [new TransactWriteItem { Put = new Put { TableName = arn, Item = Item(("pk", S("row-new")), ("v", S("never"))) } }],
            }),
            "ValidationException",
            $"1 validation error detected: Value '{arn}' at 'transactItems.1.member.put.tableName' failed to satisfy constraint: Invalid AWS region");
        var neverWritten = await there.GetItemAsync(new GetItemRequest { TableName = table, Key = NewKey(), ConsistentRead = true });
        Ensure(neverWritten.Item is null, $"the cross-Region refusal must not create the item, got {Py.Repr(neverWritten.Item)}");

        // Positive control: the same ARN, addressed from its own Region, reads and writes normally.
        await there.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = arn, Item = Item(("pk", S("row-new")), ("v", S("ok"))) } }],
        });
        var written = (await there.GetItemAsync(new GetItemRequest { TableName = table, Key = NewKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(written["v"], S("ok")), Py.Repr(written));

        var got = await there.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = arn, Key = SeedKey() } }],
        });
        Ensure(Py.Equal(got.Responses[0].Item["v"], S("seed")), Py.Repr(got.Responses.Select(r => r.Item).ToList()));
    }
}
