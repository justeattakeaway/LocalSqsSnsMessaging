// Translated from behavioral/dynamodb/check_condition_expression_gt_begins_with_in_as_put_item_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-expression-gt-begins-with-in-as-put-item-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionExpressionGtBeginsWithInAsPutItemCondition
{
    private const string Check = "condition-expression-gt-begins-with-in-as-put-item-condition";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    private static AttributeValue New() => S("put-landed");

    // (case_name, stored attr value, ConditionExpression, ExpressionAttributeValues, expect the write to land)
    private static (string CaseName, AttributeValue Stored, string Condition, Dictionary<string, AttributeValue> Values, bool ExpectPass)[] Cases() =>
    [
        ("gt_true", N("20"), "val > :threshold", Item((":threshold", N("10"))), true),
        ("gt_false", N("5"), "val > :threshold", Item((":threshold", N("10"))), false),
        ("begins_with_true", S("hello-world"), "begins_with(val, :prefix)", Item((":prefix", S("hello"))), true),
        ("begins_with_false", S("hello-world"), "begins_with(val, :prefix)", Item((":prefix", S("xyz"))), false),
        ("in_true", S("b"), "val IN (:a, :b, :c)", Item((":a", S("a")), (":b", S("b")), (":c", S("c"))), true),
        ("in_false", S("z"), "val IN (:a, :b, :c)", Item((":a", S("a")), (":b", S("b")), (":c", S("c"))), false),
    ];

    /// <summary>
    /// ConditionExpression >, begins_with and IN gate PutItem: a true condition lets the item
    /// overwrite land, and a false one gives ConditionalCheckFailedException and leaves it unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-expression-gt-begins-with-in-as-put-item-condition")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        async Task<Dictionary<string, AttributeValue>?> Stored(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        Task<PutItemResponse> TryConditionalPut(string pk, string condition, Dictionary<string, AttributeValue> values) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(pk)), ("val", New()), ("marker", S("applied"))),
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
            });

        foreach (var (caseName, stored, condition, values, expectPass) in Cases())
        {
            var pk = caseName;
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", stored)));

            if (expectPass)
            {
                await TryConditionalPut(pk, condition, values);
                var got = await Stored(pk);
                Ensure(
                    Py.Equal(got, Item(("pk", S(pk)), ("val", New()), ("marker", S("applied")))),
                    $"{caseName}: a passing conditional PutItem did not land: {Py.Repr(got)}");
            }
            else
            {
                await ExpectError(() => TryConditionalPut(pk, condition, values), Ccfe);
                var got = await Stored(pk);
                Ensure(
                    Py.Equal(got, Item(("pk", S(pk)), ("val", stored))),
                    $"{caseName}: a rejected conditional PutItem must leave the item unchanged, got {Py.Repr(got)}");
            }
        }
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the conditional PutItem to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }
}
