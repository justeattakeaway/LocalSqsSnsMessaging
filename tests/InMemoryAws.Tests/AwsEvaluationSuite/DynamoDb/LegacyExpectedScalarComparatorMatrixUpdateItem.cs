// Translated from behavioral/dynamodb/check_legacy_expected_scalar_comparator_matrix_update_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-scalar-comparator-matrix-update-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedScalarComparatorMatrixUpdateItem
{
    private const string CheckId = "legacy-expected-scalar-comparator-matrix-update-item";
    private const string ErrCcfe = "ConditionalCheckFailedException";
    private const string ErrCcfeMessage = "The conditional request failed";

    // (case_name, stored attr value, Expected clause for 'attr', expect the write to land)
    private static (string CaseName, Func<AttributeValue> Stored, Func<ExpectedAttributeValue> Condition, bool ExpectPass)[] Cases() =>
    [
        ("ne_true", () => N("5"), () => Cond("NE", N("6")), true),
        ("ne_false", () => N("5"), () => Cond("NE", N("5")), false),
        ("lt_true_bytes", () => S("B"), () => Cond("LT", S("a")), true),
        ("lt_false_bytes", () => S("a"), () => Cond("LT", S("B")), false),
        ("le_true_equal", () => N("5"), () => Cond("LE", N("5")), true),
        ("le_false", () => N("6"), () => Cond("LE", N("5")), false),
        ("gt_true_numeric", () => N("10"), () => Cond("GT", N("9")), true),
        ("gt_false_numeric", () => N("9"), () => Cond("GT", N("10")), false),
        ("ge_true_equal", () => N("5"), () => Cond("GE", N("5")), true),
        ("ge_false", () => N("4"), () => Cond("GE", N("5")), false),
        ("between_true", () => N("5"), () => Cond("BETWEEN", N("1"), N("10")), true),
        ("between_false", () => N("11"), () => Cond("BETWEEN", N("1"), N("10")), false),
        ("eq_value_comparator_true", () => S("match"), () => new ExpectedAttributeValue { Value = S("match"), ComparisonOperator = ComparisonOperator.EQ }, true),
        ("eq_value_comparator_false", () => S("mismatch"), () => new ExpectedAttributeValue { Value = S("match"), ComparisonOperator = ComparisonOperator.EQ }, false),
        ("eq_bool_true", () => Bool(true), () => Cond("EQ", Bool(true)), true),
        ("eq_bool_false", () => Bool(false), () => Cond("EQ", Bool(true)), false),
    ];

    private static ExpectedAttributeValue Cond(string op, params AttributeValue[] values) =>
        new() { ComparisonOperator = op, AttributeValueList = [.. values] };

    /// <summary>
    /// Every legacy Expected scalar comparator (NE, LT, LE, GT, GE, BETWEEN, EQ via Value and
    /// ComparisonOperator, EQ on BOOL) lets UpdateItem land when true, comparing N numerically and S
    /// by UTF-8 byte order, and gives ConditionalCheckFailedException unchanged when false.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Expected.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-scalar-comparator-matrix-update-item")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        foreach (var (caseName, stored, condition, expectPass) in Cases())
        {
            var pk = caseName;
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("attr", stored())));

            var expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["attr"] = condition() };
            var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(pk))),
                AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
                {
                    ["marker"] = new AttributeValueUpdate { Value = S("applied"), Action = AttributeAction.PUT },
                },
                Expected = expected,
            }));
            var succeeded = error is null;
            var errorCode = error?.Code;
            var errorMessage = error?.Message;

            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
            if (expectPass)
            {
                Ensure(succeeded, $"{caseName}: expected the write to land, got {Py.Repr(errorCode)}");
                Ensure(
                    Py.Equal(got, Item(("pk", S(pk)), ("attr", stored()), ("marker", S("applied")))),
                    $"{caseName}: a passing conditional UpdateItem did not land: {Py.Repr(got)}");
            }
            else
            {
                Ensure(!succeeded, $"{caseName}: expected ConditionalCheckFailedException, call succeeded");
                Ensure(errorCode == ErrCcfe, $"{caseName}: expected {Py.Repr(ErrCcfe)}, got {Py.Repr(errorCode)}");
                Ensure(errorMessage == ErrCcfeMessage, $"{caseName}: expected AWS exact CCFE message, got {Py.Repr(errorMessage)}");
                Ensure(
                    Py.Equal(got, Item(("pk", S(pk)), ("attr", stored()))),
                    $"{caseName}: a rejected UpdateItem must leave the item unchanged, got {Py.Repr(got)}");
            }
        }
    }
}
