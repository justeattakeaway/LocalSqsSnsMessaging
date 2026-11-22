// Translated from behavioral/dynamodb/check_nested_placeholder_path_missing_segment_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.nested-placeholder-path-missing-segment-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestedPlaceholderPathMissingSegmentRejected
{
    private const string Check = "nested-placeholder-path-missing-segment-rejected";

    private static readonly (string Code, string Message) InvalidPath = (
        "ValidationException",
        "The document path provided in the update expression is invalid for update");

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
    /// ADD and DELETE through a per-segment placeholder path are rejected when an
    /// intermediate segment is absent from the stored item, and the item stays unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.nested-placeholder-path-missing-segment-rejected")]
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

        var key = Item(("pk", S("x")));
        var original = Item(("pk", S("x")), ("fis", M(("isa", M(("events", N("0")))))));
        await ddb.PutItemAsync(table, original);

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD #f.#x.#e :one",
                ExpressionAttributeNames = Names(("#f", "fis"), ("#x", "tags"), ("#e", "events")),
                ExpressionAttributeValues = Item((":one", N("1"))),
            }),
            InvalidPath);
        var afterAdd = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(afterAdd, original), $"the rejected ADD must leave the item unchanged, got {Py.Repr(afterAdd)}");

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE #f.#y.#l :s",
                ExpressionAttributeNames = Names(("#f", "fis"), ("#y", "tags"), ("#l", "labels")),
                ExpressionAttributeValues = Item((":s", SS("a"))),
            }),
            InvalidPath);
        var afterDelete = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(afterDelete, original), $"the rejected DELETE must leave the item unchanged, got {Py.Repr(afterDelete)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #f.#i.#e :one",
            ExpressionAttributeNames = Names(("#f", "fis"), ("#i", "isa"), ("#e", "events")),
            ExpressionAttributeValues = Item((":one", N("1"))),
        });
        var controlled = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(controlled["fis"].M["isa"].M["events"], N("1")),
            $"the positive-control ADD on an existing segment must apply, got {Py.Repr(controlled)}");
    }
}
