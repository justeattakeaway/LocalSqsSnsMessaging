// Translated from behavioral/dynamodb/check_transact_write_items_index_key_type_mismatch_cancels_not_validates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-index-key-type-mismatch-cancels-not-validates</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsIndexKeyTypeMismatchCancelsNotValidates
{
    /// <summary>
    /// An index key of the wrong TYPE inside a transaction is not a top-level ValidationException:
    /// DynamoDB accepts the request and CANCELS it, with reason code ValidationError carrying the
    /// PutItem-style message. An engine answering a top-level ValidationException diverges.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-index-key-type-mismatch-cancels-not-validates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var exc = await Raises(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("b")), ("sk", S("1")), ("gpk", N("9"))) } },
                ],
            }),
            "a transaction whose index key has the wrong type");
        var reasons = exc.CancellationReasons() ?? [];
        Ensure(
            exc.Code == "TransactionCanceledException",
            $"a wrong-TYPE index key cancels the transaction rather than failing validation up front; got {exc.Code}");
        const string expected = "Transaction cancelled, please refer cancellation reasons for specific reasons [ValidationError]";
        Ensure(exc.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(exc.Message)}");
        var codes = reasons.Select(r => r.Code).ToList();
        Ensure(codes.SequenceEqual(["ValidationError"]), $"expected exactly one ValidationError reason, got {Py.Repr(codes)}");
        const string expectedReason = "One or more parameter values were invalid: Type mismatch for Index Key gpk "
            + "Expected: S Actual: N IndexName: gsi1";
        Ensure(reasons[0].Message == expectedReason, $"expected reason message {Py.Repr(expectedReason)}, got {Py.Repr(reasons[0].Message)}");
        Ensure(
            (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0,
            "a cancelled transaction must write nothing");
    }

    private static async Task<AwsError> Raises(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call)
        ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
