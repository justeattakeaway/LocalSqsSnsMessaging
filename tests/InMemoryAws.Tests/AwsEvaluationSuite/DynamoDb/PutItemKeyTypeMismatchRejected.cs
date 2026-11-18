// Translated from behavioral/dynamodb/check_put_item_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemKeyTypeMismatchRejected
{
    private const string CheckId = "put-item-key-type-mismatch-rejected";

    /// <summary>
    /// PutItem with a partition key value whose AttributeValue type (N) does not match
    /// the table's declared key type (S) is rejected with ValidationException, the item is
    /// never persisted, and a same-shaped correctly-typed PutItem succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-key-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);

        const int badPkNumeric = 424242;
        var err = await Error(() => ddb.PutItemAsync(
            table,
            Item(("pk", N(badPkNumeric.ToString(CultureInfo.InvariantCulture))), ("note", S("bad")))));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}");
        const string expected = "One or more parameter values were invalid: Type mismatch for key pk expected: S actual: N";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        // Effect absent: nothing was written by the rejected call.
        var scan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(scan.Count == 0, "rejected PutItem still persisted an item");

        // Positive control: a correctly-typed PutItem on the same table succeeds.
        var goodPk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(goodPk)), ("note", S("good"))));
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(goodPk))), ConsistentRead = true });
        Ensure(got.Item is not null, "positive control PutItem did not persist");
        var note = got.Item["note"].S;
        Ensure(note == "good", $"expected note='good', got {Py.Repr(note)}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        return name;
    }

    /// <summary>
    /// Call fn(); return its botocore Error. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> Error(Func<Task<PutItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }
}
