// Translated from behavioral/dynamodb/check_update_item_multiple_simultaneous_key_violations_reports_one.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-multiple-simultaneous-key-violations-reports-one</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemMultipleSimultaneousKeyViolationsReportsOne
{
    private const string Check = "update-item-multiple-simultaneous-key-violations-reports-one";
    private const string ErrValidation = "ValidationException";

    /// <summary>The Error dict of a call real AWS refuses; an unexpected success raises.</summary>
    private static async Task<AwsError> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err ?? throw Fail($"real AWS rejects this UpdateItem, but it SUCCEEDED and returned {result}");
    }

    private static Dictionary<string, string?> Dict(AwsError err) => new() { ["Code"] = err.Code, ["Message"] = err.Message };

    /// <summary>
    /// A single UpdateItem naming two independent key or index-key violations at once is
    /// refused with one ValidationException, never a merged or either/or message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-multiple-simultaneous-key-violations-reports-one")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-mkv-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsisk", ScalarAttributeType.S),
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
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsisk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("p1")), ("sk", S("orig-sk")));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("orig-sk")), ("gpk", S("g1")), ("lsisk", S("l1")), ("note", S("orig"))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // Positive control: an ordinary UpdateItem on this item succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET note = :v",
            ExpressionAttributeValues = Item((":v", S("updated"))),
        });
        var baseline = await Read();
        Ensure(Py.Equal(baseline["note"], S("updated")), $"positive control failed: {Py.Repr(baseline)}");

        // Scenario 1: a range-key rename and a GSI key type mismatch in the same request.
        var err1 = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET sk = :newsk, gpk = :badgpk",
            ExpressionAttributeValues = Item((":newsk", S("new-sk")), (":badgpk", N("9"))),
        }));
        Ensure(err1.Code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(Dict(err1))}");
        const string expected1 =
            "One or more parameter values were invalid: Cannot update attribute sk. " +
            "This attribute is part of the key";
        Ensure(err1.Message == expected1, $"expected message {Py.Repr(expected1)}, got {Py.Repr(err1.Message)}");

        var after1 = await Read();
        Ensure(
            Py.Equal(after1, baseline),
            "a request rejected for naming two violations must leave the item fully " +
            $"unchanged, got {Py.Repr(after1)}");

        // Scenario 2: a GSI key type mismatch and an LSI key type mismatch in the same request.
        var err2 = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET gpk = :badgpk2, lsisk = :badlsisk",
            ExpressionAttributeValues = Item((":badgpk2", N("1")), (":badlsisk", N("2"))),
        }));
        Ensure(err2.Code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(Dict(err2))}");
        const string expected2 =
            "One or more parameter values were invalid: Type mismatch for Index Key gpk " +
            "Expected: S Actual: N IndexName: gsi1";
        Ensure(err2.Message == expected2, $"expected message {Py.Repr(expected2)}, got {Py.Repr(err2.Message)}");

        var after2 = await Read();
        Ensure(
            Py.Equal(after2, baseline),
            "a request rejected for naming two bad index keys must leave the item fully " +
            $"unchanged, got {Py.Repr(after2)}");
    }
}
