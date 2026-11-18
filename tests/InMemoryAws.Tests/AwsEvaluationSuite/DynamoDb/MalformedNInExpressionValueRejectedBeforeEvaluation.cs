// Translated from behavioral/dynamodb/check_malformed_n_in_expression_value_rejected_before_evaluation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.malformed-n-in-expression-value-rejected-before-evaluation</summary>
[Category("AwsEvaluationSuite")]
public sealed class MalformedNInExpressionValueRejectedBeforeEvaluation
{
    private const string CheckId = "malformed-n-in-expression-value-rejected-before-evaluation";
    private const string ErrValidation = "ValidationException";
    private const string ErrConditional = "ConditionalCheckFailedException";

    // Each form is malformed in a different way: unparsable mantissa, an empty string, and a
    // mantissa that parses but overflows the documented magnitude range.
    private static readonly string[] Malformed = ["abc", "", "1e99999"];

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// A malformed N in a ConditionExpression, legacy Expected or UpdateExpression value is
    /// ValidationException, not ConditionalCheckFailedException, even when the condition would
    /// also fail on a well-formed value -- validation precedes evaluation, item untouched.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    /// </summary>
    [Test]
    [DisplayName("dynamodb.malformed-n-in-expression-value-rejected-before-evaluation")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("v", N("5")), ("note", S("orig"))));

        async Task Unchanged()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            Ensure(Py.Equal(item, Item(("pk", S("item")), ("v", N("5")), ("note", S("orig")))), $"item must be untouched by every rejected call, got {Py.Repr(item)}");
        }

        Dictionary<string, AttributeValue> Changed() => Item(("pk", S("item")), ("v", N("99")), ("note", S("changed")));

        // --- positive control: a WELL-FORMED but non-matching condition fails with
        // ConditionalCheckFailedException, proving the two errors are distinguishable and that the
        // malformed-value rejections below are not merely "this condition never matches". ---
        var (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Changed(),
            ConditionExpression = "v = :cond",
            ExpressionAttributeValues = Item((":cond", N("7"))),
        }));
        Ensure(code == ErrConditional, $"well-formed non-matching condition must fail with {ErrConditional}, got {code}");
        Ensure(message == "The conditional request failed", $"expected AWS's exact message, got {Py.Repr(message)}");
        await Unchanged();

        foreach (var mv in Malformed)
        {
            // --- malformed N in a ConditionExpression value, against a condition that would also
            // fail on a well-formed value (v=5 never equals 7) ---
            (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Changed(),
                ConditionExpression = "v = :cond",
                ExpressionAttributeValues = Item((":cond", N(mv))),
            }));
            Ensure(code == ErrValidation, $"malformed N {Py.Repr(mv)} in ConditionExpression value must be {ErrValidation}, got {code}");
            var expected = mv == "1e99999"
                ? "1 validation error detected: Number overflow. Attempting to store a number with magnitude larger than supported range"
                : $"1 validation error detected: The parameter cannot be converted to a numeric value: {mv}";
            Ensure(message == expected, $"expected AWS's exact message, got {Py.Repr(message)}");
            await Unchanged();

            // --- malformed N in a legacy Expected value, against a condition that would also fail
            // on a well-formed value (v=5 never equals 7) ---
            (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Changed(),
                Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["v"] = new ExpectedAttributeValue { Value = N(mv) } },
            }));
            Ensure(code == ErrValidation, $"malformed N {Py.Repr(mv)} in legacy Expected value must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact message, got {Py.Repr(message)}");
            await Unchanged();

            // --- malformed N in an UpdateExpression SET value, guarded by a ConditionExpression
            // that would also fail on a well-formed value ---
            (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET note = :val",
                ConditionExpression = "v = :cond",
                ExpressionAttributeValues = Item((":val", N(mv)), (":cond", N("7"))),
            }));
            Ensure(code == ErrValidation, $"malformed N {Py.Repr(mv)} in UpdateExpression value must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact message, got {Py.Repr(message)}");
            await Unchanged();
        }

        // Control still works after every rejection.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET note = :val",
            ExpressionAttributeValues = Item((":val", S("final"))),
        });
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(final["note"], S("final")), $"control update must still succeed, got {Py.Repr(final)}");
    }
}
