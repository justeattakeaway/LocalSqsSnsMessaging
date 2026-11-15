// Translated from behavioral/dynamodb/check_transact_write_items_update_key_and_empty_index_key_rejections.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-key-and-empty-index-key-rejections</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateKeyAndEmptyIndexKeyRejections
{
    private const string Check = "transact-write-items-update-key-and-empty-index-key-rejections";

    private static async Task<AwsError> Raises(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// A TransactWriteItems Update that SETs a key attribute is a cancellation, carrying a
    /// ValidationError cancellation reason; one that SETs a GSI key attribute to an empty string is
    /// up-front validation instead, a top-level ValidationException. Neither writes anything.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-key-and-empty-index-key-rejections")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);
        var (pk, sk, gpk) = (FidelityContext.UniqueName("item-"), "s1", "g1");
        var original = Item(("pk", S(pk)), ("sk", S(sk)), ("gpk", S(gpk)), ("val", N("1")));
        await ddb.PutItemAsync(table, original);

        var keyExc = await Raises(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S(pk)), ("sk", S(sk))),
                            UpdateExpression = "SET pk = :v",
                            ExpressionAttributeValues = Item((":v", S("other"))),
                        },
                    },
                ],
            }),
            "an Update whose UpdateExpression SETs the hash key attribute");
        Ensure(
            keyExc.Code == "TransactionCanceledException",
            $"updating a key attribute: expected TransactionCanceledException, got {Py.Repr(keyExc.Code)}");
        var keyReasons = keyExc.CancellationReasons() ?? [];
        Ensure(keyReasons.Count == 1, $"expected exactly one cancellation reason, got {ReasonsRepr(keyReasons)}");
        Ensure(
            keyReasons[0].Code == "ValidationError",
            $"updating a key attribute: expected cancellation reason ValidationError, got {ReasonRepr(keyReasons[0])}");
        const string expectedKeyMessage =
            "One or more parameter values were invalid: Cannot update attribute pk. "
            + "This attribute is part of the key";
        Ensure(
            keyReasons[0].Message == expectedKeyMessage,
            $"updating a key attribute gave reason message {Py.Repr(keyReasons[0].Message)}, expected {Py.Repr(expectedKeyMessage)}");

        var indexExc = await Raises(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S(pk)), ("sk", S(sk))),
                            UpdateExpression = "SET gpk = :e",
                            ExpressionAttributeValues = Item((":e", S(""))),
                        },
                    },
                ],
            }),
            "an Update whose UpdateExpression SETs a GSI key attribute to an empty string");
        var indexTypeName = indexExc.Exception.GetType().Name;
        Ensure(
            indexTypeName != "TransactionCanceledException",
            $"an empty index key is up-front validation, not a cancellation; got {indexTypeName}");
        Ensure(
            indexExc.Code == "ValidationException",
            $"empty GSI key via Update: expected ValidationException, got {Py.Repr(indexExc.Code)}");
        const string expectedIndexMessage =
            "One or more parameter values are not valid. The update expression attempted to update a "
            + "secondary index key to a value that is not supported. The AttributeValue for a key "
            + "attribute cannot contain an empty string value.";
        Ensure(
            indexExc.Message == expectedIndexMessage,
            $"empty GSI key via Update gave message {Py.Repr(indexExc.Message)}, expected {Py.Repr(expectedIndexMessage)}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, original), $"a rejected transaction must write nothing; item is now {Py.Repr(got)}");
    }

    private static string ReasonRepr(CancellationReason r)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Item is not null)
        {
            d["Item"] = r.Item;
        }

        d["Code"] = r.Code;
        if (r.Message is not null)
        {
            d["Message"] = r.Message;
        }

        return Py.Repr(d);
    }

    private static string ReasonsRepr(List<CancellationReason> reasons) => "[" + string.Join(", ", reasons.Select(ReasonRepr)) + "]";

    /// <summary>A table with hash+range key and a GSI on gpk (S), projecting all attributes.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
        return name;
    }
}
