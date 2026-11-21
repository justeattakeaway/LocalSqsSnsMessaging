// Translated from behavioral/dynamodb/check_update_item_add_empty_number_and_binary_set_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-empty-number-and-binary-set-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddEmptyNumberAndBinarySetOperandRejected
{
    private const string Check = "update-item-add-empty-number-and-binary-set-operand-rejected";

    private static readonly (string Code, string Message) EmptyNs = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "An number set  may not be empty");

    private static readonly (string Code, string Message) EmptyBs = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "Binary sets should not be empty");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; raises on success.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// UpdateItem's ADD action rejects an empty number set and an empty binary set operand
    /// with AWS's exact per-type message, leaving the target attribute unchanged; a same-typed
    /// operand with a member still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-empty-number-and-binary-set-operand-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var nsKey = Item(("pk", S("ns")));
        var bsKey = Item(("pk", S("bs")));
        await ddb.PutItemAsync(table, Item(("pk", S("ns")), ("nums", NS("1"))));
        await ddb.PutItemAsync(table, Item(("pk", S("bs")), ("blobs", BS("a"u8.ToArray()))));

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = nsKey,
                UpdateExpression = "ADD nums :e",
                ExpressionAttributeValues = Item((":e", NS())),
            }),
            EmptyNs);

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = bsKey,
                UpdateExpression = "ADD blobs :e",
                ExpressionAttributeValues = Item((":e", BS())),
            }),
            EmptyBs);

        var unchangedNs = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = nsKey, ConsistentRead = true })).Item;
        Ensure(
            unchangedNs["nums"].NS.SequenceEqual(["1"]),
            $"the rejected NS ADD must leave the attribute untouched, got {Py.Repr(unchangedNs["nums"])}");
        var unchangedBs = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = bsKey, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchangedBs["blobs"], BS("a"u8.ToArray())),
            $"the rejected BS ADD must leave the attribute untouched, got {Py.Repr(unchangedBs["blobs"])}");

        // Positive control: the same operand shape with a member applies, for both types.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = nsKey,
            UpdateExpression = "ADD nums :e",
            ExpressionAttributeValues = Item((":e", NS("2"))),
        });
        var grownNs = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = nsKey, ConsistentRead = true })).Item;
        Ensure(
            new HashSet<string>(grownNs["nums"].NS, StringComparer.Ordinal).SetEquals(["1", "2"]),
            $"the positive-control NS ADD with a non-empty set must apply, got {Py.Repr(grownNs["nums"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = bsKey,
            UpdateExpression = "ADD blobs :e",
            ExpressionAttributeValues = Item((":e", BS("b"u8.ToArray()))),
        });
        var grownBs = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = bsKey, ConsistentRead = true })).Item;
        Ensure(
            new HashSet<string>(grownBs["blobs"].BS.Select(b => Convert.ToBase64String(b.ToArray())), StringComparer.Ordinal)
                .SetEquals([Convert.ToBase64String("a"u8.ToArray()), Convert.ToBase64String("b"u8.ToArray())]),
            $"the positive-control BS ADD with a non-empty set must apply, got {Py.Repr(grownBs["blobs"])}");
    }
}
