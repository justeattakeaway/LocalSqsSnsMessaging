// Translated from behavioral/dynamodb/check_putitem_condition_attribute_type_matches_bool_null_string_binary_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-attribute-type-matches-bool-null-string-binary-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionAttributeTypeMatchesBoolNullStringBinarySet
{
    private const string Check = "putitem-condition-attribute-type-matches-bool-null-string-binary-set";

    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    // (label, the attribute carrying the type under test, its right type, a different type to
    // condition on for the negative control)
    private static (string Label, string AttrName, Func<AttributeValue> AttrValue, string RightType, string WrongType)[] Cases() =>
    [
        ("bool", "flag", () => Bool(true), "BOOL", "N"),
        ("null", "missing", Null, "NULL", "S"),
        ("str", "name", () => S("Mary Major"), "S", "N"),
        ("bset", "codes", () => BS([0x01, 0x02]), "BS", "S"),
    ];

    /// <summary>
    /// A PutItem ConditionExpression attribute_type(#n, :t) matches BOOL, NULL, S and BS
    /// attributes by their stored type: the overwrite applies when :t names that type, and is
    /// refused with ConditionalCheckFailedException, item unchanged, when :t names a different one.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-attribute-type-matches-bool-null-string-binary-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbcat-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (label, attrName, attrValue, rightType, wrongType) in Cases())
        {
            var key = Item(("pk", S($"item-{label}")));
            var initial = Item(("pk", S($"item-{label}")), (attrName, attrValue()), ("seq", N("1")));
            await ddb.PutItemAsync(table, initial);
            var names = Names(("#n", attrName));

            // The condition is false: the attribute's stored type is `right_type`, not `wrong_type`,
            // so the write is refused and the item is left exactly as it was.
            await ExpectError(
                () => ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = table,
                    Item = Item(("pk", S($"item-{label}")), (attrName, attrValue()), ("seq", N("2"))),
                    ConditionExpression = "attribute_type(#n, :t)",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = Item((":t", S(wrongType))),
                }),
                CondFailed);
            var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(unchanged, initial),
                $"PutItem under a failing attribute_type({Py.Repr(attrName)}, {Py.Repr(wrongType)}) condition " +
                $"left {Py.Repr(unchanged)}, expected {Py.Repr(initial)} unchanged");

            // The condition is true: the attribute's stored type IS `right_type`, so the write
            // replaces the item.
            var replacement = Item(("pk", S($"item-{label}")), (attrName, attrValue()), ("seq", N("2")));
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = replacement,
                ConditionExpression = "attribute_type(#n, :t)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = Item((":t", S(rightType))),
            });
            var replaced = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(replaced, replacement),
                $"PutItem under a holding attribute_type({Py.Repr(attrName)}, {Py.Repr(rightType)}) condition " +
                $"left {Py.Repr(replaced)}, expected {Py.Repr(replacement)}");
        }
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
