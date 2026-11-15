// Translated from behavioral/dynamodb/check_legacy_expected_contains_in_null_comparator_matrix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-contains-in-null-comparator-matrix</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedContainsInNullComparatorMatrix
{
    private const string CheckId = "legacy-expected-contains-in-null-comparator-matrix";
    private const string ErrCcfe = "ConditionalCheckFailedException";
    private const string ErrCcfeMessage = "The conditional request failed";
    private static readonly string[] Operations = ["Put", "Update", "Delete"];

    private static ExpectedAttributeValue Cond(string op, params AttributeValue[] values) =>
        values.Length == 0
            ? new ExpectedAttributeValue { ComparisonOperator = op }
            : new ExpectedAttributeValue { ComparisonOperator = op, AttributeValueList = [.. values] };

    /// <summary>(case_name, stored attr value, Expected clause for 'attr', expect the write to land)</summary>
    private static (string CaseName, Func<AttributeValue>? Stored, Func<ExpectedAttributeValue> Condition, bool ExpectPass)[] Cases() =>
    [
        ("contains_substring_true", () => S("hello world"), () => Cond("CONTAINS", S("world")), true),
        ("contains_substring_false", () => S("hello world"), () => Cond("CONTAINS", S("xyz")), false),
        ("contains_set_element_true", () => SS("x", "y"), () => Cond("CONTAINS", S("x")), true),
        ("contains_set_element_false", () => SS("x", "y"), () => Cond("CONTAINS", S("z")), false),
        ("not_contains_true", () => S("hello world"), () => Cond("NOT_CONTAINS", S("xyz")), true),
        ("not_contains_false", () => S("hello world"), () => Cond("NOT_CONTAINS", S("world")), false),
        ("in_scalar_true", () => S("b"), () => Cond("IN", S("a"), S("b"), S("c")), true),
        ("in_scalar_false", () => S("z"), () => Cond("IN", S("a"), S("b"), S("c")), false),
        // IN never matches a set attribute: with the element present or absent, the write is
        // ConditionalCheckFailedException.
        ("in_against_set_element_present", () => SS("x", "y"), () => Cond("IN", S("x")), false),
        ("in_against_set_element_absent", () => SS("x", "y"), () => Cond("IN", S("z")), false),
        ("begins_with_false", () => S("hello"), () => Cond("BEGINS_WITH", S("xyz")), false),
        ("null_true_absent", null, () => Cond("NULL"), true),
        ("null_false_present", () => S("present"), () => Cond("NULL"), false),
        ("not_null_true_present", () => S("present"), () => Cond("NOT_NULL"), true),
        ("not_null_false_absent", null, () => Cond("NOT_NULL"), false),
    ];

    private static AttributeValue New() => S("write-landed");

    private static async Task SetupItem(IAmazonDynamoDB ddb, string table, string pk, Func<AttributeValue>? stored)
    {
        var item = Item(("pk", S(pk)));
        if (stored is not null)
        {
            item["attr"] = stored();
        }

        await ddb.PutItemAsync(table, item);
    }

    private static async Task<(string Kind, string? Code, string? Message)> Apply(IAmazonDynamoDB ddb, string table, string op, string pk, ExpectedAttributeValue condition)
    {
        var expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["attr"] = condition };
        var error = await FidelityContext.CaptureError(async () =>
        {
            if (op == "Put")
            {
                await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(pk)), ("attr", New()), ("marker", S("applied"))), Expected = expected });
            }
            else if (op == "Delete")
            {
                await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Item(("pk", S(pk))), Expected = expected });
            }
            else
            {
                await ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Item(("pk", S(pk))),
                    AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
                    {
                        ["marker"] = new AttributeValueUpdate { Value = S("applied"), Action = AttributeAction.PUT },
                    },
                    Expected = expected,
                });
            }
        });
        return error is null ? ("ok", null, null) : ("error", error.Code, error.Message);
    }

    /// <summary>
    /// Legacy Expected CONTAINS, NOT_CONTAINS, IN, a failing BEGINS_WITH, NULL and NOT_NULL let a
    /// write land when true and give ConditionalCheckFailedException unchanged when false, including
    /// CONTAINS matching a set element and IN never matching a scalar against a set attribute.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Expected.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-contains-in-null-comparator-matrix")]
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
            foreach (var op in Operations)
            {
                var pk = $"{caseName}-{op}";
                await SetupItem(ddb, table, pk, stored);
                var (kind, code, message) = await Apply(ddb, table, op, pk, condition());
                var label = $"{caseName}/{op}";
                var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

                if (expectPass)
                {
                    Ensure(kind == "ok", $"{label}: expected the write to land, got {code ?? "None"}: {Py.Repr(message)}");
                    if (op == "Delete")
                    {
                        Ensure(got is null, $"{label}: a passing conditional DeleteItem left an item: {Py.Repr(got)}");
                    }
                    else if (op == "Put")
                    {
                        Ensure(
                            Py.Equal(got, Item(("pk", S(pk)), ("attr", New()), ("marker", S("applied")))),
                            $"{label}: a passing conditional PutItem did not land: {Py.Repr(got)}");
                    }
                    else
                    {
                        var expectedItem = Item(("pk", S(pk)), ("marker", S("applied")));
                        if (stored is not null)
                        {
                            expectedItem["attr"] = stored();
                        }

                        Ensure(Py.Equal(got, expectedItem), $"{label}: a passing conditional UpdateItem did not land: {Py.Repr(got)}");
                    }
                }
                else
                {
                    Ensure(kind == "error", $"{label}: expected {ErrCcfe}, call succeeded");
                    Ensure(code == ErrCcfe, $"{label}: expected {Py.Repr(ErrCcfe)}, got {Py.Repr(code)}");
                    Ensure(message == ErrCcfeMessage, $"{label}: expected AWS exact CCFE message, got {Py.Repr(message)}");
                    var expectedItem = Item(("pk", S(pk)));
                    if (stored is not null)
                    {
                        expectedItem["attr"] = stored();
                    }

                    Ensure(Py.Equal(got, expectedItem), $"{label}: a rejected {op}Item must leave the item unchanged, got {Py.Repr(got)}");
                }
            }
        }
    }
}
