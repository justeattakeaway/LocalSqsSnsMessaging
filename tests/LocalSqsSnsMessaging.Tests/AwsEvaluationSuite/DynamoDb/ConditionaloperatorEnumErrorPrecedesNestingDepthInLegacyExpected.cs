// Translated from behavioral/dynamodb/check_conditionaloperator_enum_error_precedes_nesting_depth_in_legacy_expected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conditionaloperator-enum-error-precedes-nesting-depth-in-legacy-expected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionaloperatorEnumErrorPrecedesNestingDepthInLegacyExpected
{
    private const string Check = "conditionaloperator-enum-error-precedes-nesting-depth-in-legacy-expected";
    private const string ErrValidation = "ValidationException";
    private const string MsgDepth = "1 validation error detected: Nesting Levels have exceeded supported limits: " +
                                    "Attributes in the item have nested levels beyond supported limit";
    private const string MsgBadop = "1 validation error detected: Failed to satisfy constraint: Member must satisfy " +
                                    "enum value set: [ALL, OR]";

    /// <summary>
    /// A legacy Expected map whose AttributeValueList holds a document 39 levels past the
    /// nesting limit, combined with an invalid ConditionalOperator value, reports the
    /// ConditionalOperator enum error, not the nesting-depth error the deep value alone raises.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conditionaloperator-enum-error-precedes-nesting-depth-in-legacy-expected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        static AttributeValue Deep() => Nest(39);
        static AttributeValue Shallow() => S("leaf");

        Task Seed(string key) => ddb.PutItemAsync(table, Item(("pk", S(key)), ("a", S("va")), ("b", S("vb"))));

        async Task<Dictionary<string, AttributeValue>> Current(string key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;

        static Dictionary<string, ExpectedAttributeValue> Expected(AttributeValue a, AttributeValue b) => new(StringComparer.Ordinal)
        {
            ["a"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [a] },
            ["b"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [b] },
        };

        // Positive control: a valid 2-condition Expected with ConditionalOperator='AND' still
        // performs the conditional overwrite.
        await Seed("ctrl");
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("ctrl")), ("a", S("va2")), ("b", S("vb2"))),
            Expected = Expected(S("va"), S("vb")),
            ConditionalOperator = ConditionalOperator.AND,
        });
        Ensure(Py.Equal((await Current("ctrl"))["a"], S("va2")), "a valid conditional put must perform its overwrite");

        // Combined defect: a deep comparison value plus an invalid ConditionalOperator.
        await Seed("combo");
        var (code, message) = await CodeAndMessage(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("combo")), ("a", S("overwritten")), ("b", S("vb"))),
            Expected = Expected(Deep(), Shallow()),
            ConditionalOperator = new ConditionalOperator("XOR"),
        }));
        Ensure(code == ErrValidation, $"combo: expected {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgBadop, $"combo: expected the ConditionalOperator enum message, got {Py.Repr(message)}");
        Ensure(
            Py.Equal(await Current("combo"), Item(("pk", S("combo")), ("a", S("va")), ("b", S("vb")))),
            "combo: the rejected put must leave the existing item untouched");

        // The deep value alone (valid ConditionalOperator) raises the nesting-depth error instead.
        await Seed("deep-only");
        (code, message) = await CodeAndMessage(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("deep-only")), ("a", S("overwritten")), ("b", S("vb"))),
            Expected = Expected(Deep(), Shallow()),
            ConditionalOperator = ConditionalOperator.OR,
        }));
        Ensure(code == ErrValidation, $"deep-only: expected {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgDepth, $"deep-only: expected the nesting-depth message, got {Py.Repr(message)}");
        Ensure(
            Py.Equal(await Current("deep-only"), Item(("pk", S("deep-only")), ("a", S("va")), ("b", S("vb")))),
            "deep-only: the rejected put must leave the existing item untouched");

        // The bad ConditionalOperator alone (shallow comparison values) raises the same enum error
        // as the combined case, confirming the enum check wins, not the depth check.
        await Seed("badop-only");
        (code, message) = await CodeAndMessage(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("badop-only")), ("a", S("overwritten")), ("b", S("vb"))),
            Expected = Expected(Shallow(), Shallow()),
            ConditionalOperator = new ConditionalOperator("XOR"),
        }));
        Ensure(code == ErrValidation, $"badop-only: expected {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgBadop, $"badop-only: expected the ConditionalOperator enum message, got {Py.Repr(message)}");
        Ensure(
            Py.Equal(await Current("badop-only"), Item(("pk", S("badop-only")), ("a", S("va")), ("b", S("vb")))),
            "badop-only: the rejected put must leave the existing item untouched");
    }

    /// <summary>A chain of `levels` maps around one scalar leaf, `levels + 1` levels deep in total.</summary>
    private static AttributeValue Nest(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("k", value));
        }

        return value;
    }

    /// <summary>fn()'s ValidationException (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null ? (error.Code, error.Message) : throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }
}
