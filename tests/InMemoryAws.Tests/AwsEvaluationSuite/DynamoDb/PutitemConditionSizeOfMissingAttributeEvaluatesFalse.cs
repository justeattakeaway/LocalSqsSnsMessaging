// Translated from behavioral/dynamodb/check_putitem_condition_size_of_missing_attribute_evaluates_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-size-of-missing-attribute-evaluates-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionSizeOfMissingAttributeEvaluatesFalse
{
    private const string Check = "putitem-condition-size-of-missing-attribute-evaluates-false";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A PutItem ConditionExpression of 'size(note) = :zero' against an item that never carries
    /// 'note' evaluates false (ConditionalCheckFailedException); once 'note' is the empty string,
    /// the same condition is true.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-size-of-missing-attribute-evaluates-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-sizemiss-");
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

        // The item exists but never carries "note": it is missing, not an empty string.
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("target")));
        await ddb.PutItemAsync(table, Key());
        Dictionary<string, AttributeValue> Zero() => Item((":zero", N("0")));

        // Negative: size() of a missing attribute is undefined, so '= 0' is false and the write
        // that would set "note" under that condition is refused.
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("target")), ("note", S("first"))),
                ConditionExpression = "size(note) = :zero",
                ExpressionAttributeValues = Zero(),
            }),
            Ccf);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, Key()),
            "PutItem rejected under 'size(note) = :zero' on a missing attribute must leave the item " +
            $"unchanged, got {Py.Repr(unchanged)}");

        // Positive control: once "note" is the empty string (size 0, present), the identical
        // condition is true and the write applies.
        var withEmptyNote = Item(("pk", S("target")), ("note", S("")));
        await ddb.PutItemAsync(table, withEmptyNote);
        var second = Item(("pk", S("target")), ("note", S("")), ("mark", S("second")));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = second,
            ConditionExpression = "size(note) = :zero",
            ExpressionAttributeValues = Zero(),
        });
        var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(landed, second),
            "PutItem under 'size(note) = :zero' with note already the empty string must apply, " +
            $"got {Py.Repr(landed)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }
}
