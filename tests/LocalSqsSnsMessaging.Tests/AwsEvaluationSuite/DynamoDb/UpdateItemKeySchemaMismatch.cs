// Translated from behavioral/dynamodb/check_update_item_key_schema_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-key-schema-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemKeySchemaMismatch
{
    private const string CheckId = "update-item-key-schema-mismatch";
    private const string ErrValidation = "ValidationException";
    private const string KeyMismatch = "The provided key element does not match the schema";

    /// <summary>
    /// UpdateItem against a table with a composite (hash+range) key schema, called with a Key that
    /// omits the range key entirely -- or that names an attribute not in the schema -- must be
    /// rejected with ValidationException, not treated as "close enough".
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-key-schema-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var fullKey = Item(("pk", S("schema-mismatch-1")), ("sk", S("sort-1")));
        await ddb.PutItemAsync(t, Item(("pk", S("schema-mismatch-1")), ("sk", S("sort-1")), ("v", S("orig"))));

        // Positive control: an UpdateItem using the *correct* composite key must actually take
        // effect -- otherwise a target that unconditionally rejects UpdateItem would pass the
        // negatives below for the wrong reason.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = fullKey,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S("live"))),
        });
        var live = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = fullKey, ConsistentRead = true })).Item;
        Ensure(live["v"].S == "live", $"positive control: expected update to take effect, got {Py.Repr(live.GetValueOrDefault("v"))}");

        // Missing the range key ("sk") entirely -- does not match the declared KeySchema.
        var err = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("schema-mismatch-1"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S("hacked"))),
        }));
        Ensure(err.Code == ErrValidation, $"expected {ErrValidation}, got {ErrorRepr(err)}");
        Ensure(err.Message == KeyMismatch, $"expected message {Py.Repr(KeyMismatch)}, got {Py.Repr(err.Message)}");

        // A key containing an attribute that isn't part of the schema must be rejected the same way.
        var err2 = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("schema-mismatch-1")), ("not_a_key_attr", S("z"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S("hacked2"))),
        }));
        Ensure(err2.Code == ErrValidation, $"expected {ErrValidation}, got {ErrorRepr(err2)}");
        Ensure(err2.Message == KeyMismatch, $"expected message {Py.Repr(KeyMismatch)}, got {Py.Repr(err2.Message)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = fullKey, ConsistentRead = true })).Item;
        Ensure(unchanged["v"].S == "live", "a rejected mismatched-key update must not mutate the real item");

        // The same mismatch, reached through BatchWriteItem: a PutRequest whose Item carries the
        // range key under the wrong scalar type (N where the schema declares S) is a key-schema
        // mismatch too, not a type-coercion opportunity.
        var err3 = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>
            {
                [t] =
                [
                    new WriteRequest
                    {
                        PutRequest = new PutRequest
                        {
                            Item = Item(("pk", S("schema-mismatch-1")), ("sk", N("1")), ("v", S("hacked3"))),
                        },
                    },
                ],
            },
        }));
        Ensure(err3.Code == ErrValidation, $"expected {ErrValidation}, got {ErrorRepr(err3)}");
        Ensure(err3.Message == KeyMismatch, $"expected message {Py.Repr(KeyMismatch)}, got {Py.Repr(err3.Message)}");

        var stillUnchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = fullKey, ConsistentRead = true })).Item;
        Ensure(stillUnchanged["v"].S == "live", "a rejected BatchWriteItem key-type mismatch must not mutate the real item");
    }

    /// <summary>The error of a call real AWS refuses; an unexpected success raises.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this UpdateItem, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static string ErrorRepr(AwsError error) =>
        Py.Repr(new Dictionary<string, object?> { ["Message"] = error.Message, ["Code"] = error.Code });
}
