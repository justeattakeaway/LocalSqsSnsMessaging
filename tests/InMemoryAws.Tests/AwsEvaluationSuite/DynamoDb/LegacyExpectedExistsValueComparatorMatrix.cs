// Translated from behavioral/dynamodb/check_legacy_expected_exists_value_comparator_matrix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-exists-value-comparator-matrix</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedExistsValueComparatorMatrix
{
    private const string CheckId = "legacy-expected-exists-value-comparator-matrix";
    private const string ErrValidation = "ValidationException";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private const string ErrCcfeMessage = "The conditional request failed";
    private static readonly string[] Operations = ["Put", "Update", "Delete"];

    private static AttributeValue Match() => S("match");

    private static AttributeValue Mismatch() => S("mismatch");

    private static AttributeValue New() => S("new");

    private static (string Kind, string? Message) Validation(string detail) =>
        ("validation", $"1 validation error detected: One or more parameter values were invalid: {detail}");

    private static readonly (string Kind, string? Message) Ok = ("ok", null);
    private static readonly (string Kind, string? Message) CcfeOutcome = ("ccfe", null);

    // Every legacy Expected shape for one attribute ('attr'): whether 'Exists' is given (and its
    // value), whether 'Value' is given, whether the ComparisonOperator form is given, the
    // attribute's precondition state, and the outcome AWS gives for that combination.
    private static (string CaseName, Func<ExpectedAttributeValue> Expected, string State, (string Kind, string? Message) Outcome)[] Cases() =>
    [
        ("exists_true_value_match", () => new() { Exists = true, Value = Match() }, "match", Ok),
        ("exists_true_value_mismatch", () => new() { Exists = true, Value = Match() }, "mismatch", CcfeOutcome),
        ("exists_true_no_value", () => new() { Exists = true }, "match", Validation("Value must be provided when Exists is true for Attribute: attr")),
        ("exists_false_with_value", () => new() { Exists = false, Value = Match() }, "match", Validation("Value cannot be used when Exists is false for Attribute: attr")),
        ("exists_false_bare_absent", () => new() { Exists = false }, "absent", Ok),
        ("exists_false_bare_present", () => new() { Exists = false }, "match", CcfeOutcome),
        (
            "exists_true_plus_comparator",
            () => new() { Exists = true, ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [Match()] },
            "match",
            Validation("Exists and ComparisonOperator cannot be used together for Attribute: attr")),
        (
            "exists_false_plus_comparator",
            () => new() { Exists = false, ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [Match()] },
            "match",
            Validation("Exists and ComparisonOperator cannot be used together for Attribute: attr")),
        ("comparator_alone_match", () => new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [Match()] }, "match", Ok),
        ("comparator_alone_on_missing", () => new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [Match()] }, "absent", CcfeOutcome),
        (
            "value_plus_comparator_no_exists",
            () => new() { Value = Match(), ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [Match()] },
            "match",
            Validation("Value and AttributeValueList cannot be used together for Attribute: attr")),
        ("empty_expected_entry", () => new(), "match", Validation("Value must be provided when Exists is null for Attribute: attr")),
        ("implicit_exists_match", () => new() { Value = Match() }, "match", Ok),
        ("implicit_exists_mismatch", () => new() { Value = Match() }, "mismatch", CcfeOutcome),
    ];

    private static async Task SetupItem(IAmazonDynamoDB ddb, string table, string pk, string state)
    {
        var item = Item(("pk", S(pk)));
        if (state == "match")
        {
            item["attr"] = Match();
        }
        else if (state == "mismatch")
        {
            item["attr"] = Mismatch();
        }

        await ddb.PutItemAsync(table, item);
    }

    /// <summary>Run one legacy-Expected-guarded call; return ('ok', None, None) or ('error', code, msg).</summary>
    private static async Task<(string Kind, string? Code, string? Message)> Apply(IAmazonDynamoDB ddb, string table, string op, string pk, ExpectedAttributeValue condition)
    {
        var expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["attr"] = condition };
        var error = await FidelityContext.CaptureError(async () =>
        {
            if (op == "Put")
            {
                var item = Item(("pk", S(pk)), ("attr", New()), ("marker", S("applied")));
                await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item, Expected = expected });
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
    /// Every legacy Expected shape (Exists/Value/ComparisonOperator present or absent), against
    /// an attribute present and absent, is accepted, ConditionalCheckFailedException or
    /// ValidationException identically on PutItem, UpdateItem and DeleteItem.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Expected.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-exists-value-comparator-matrix")]
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

        foreach (var (caseName, expected, state, outcome) in Cases())
        {
            foreach (var op in Operations)
            {
                var pk = $"{caseName}-{op}";
                await SetupItem(ddb, table, pk, state);
                var (kind, code, message) = await Apply(ddb, table, op, pk, expected());
                var label = $"{caseName}/{op}";

                if (outcome.Kind == "ok")
                {
                    Ensure(kind == "ok", $"{label}: expected success, got {code ?? "None"}: {Py.Repr(message)}");
                }
                else if (outcome.Kind == "ccfe")
                {
                    Ensure(kind == "error", $"{label}: expected {ErrConditionalCheckFailed}, call succeeded");
                    Ensure(code == ErrConditionalCheckFailed, $"{label}: expected {ErrConditionalCheckFailed}, got {code}");
                    Ensure(message == ErrCcfeMessage, $"{label}: expected AWS exact CCFE message, got {Py.Repr(message)}");
                }
                else
                {
                    Ensure(kind == "error", $"{label}: expected {ErrValidation}, call succeeded");
                    Ensure(code == ErrValidation, $"{label}: expected {ErrValidation}, got {code}");
                    Ensure(message == outcome.Message, $"{label}: expected AWS exact ValidationException message, got {Py.Repr(message)}");
                }

                var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
                if (outcome.Kind == "ok")
                {
                    if (op == "Delete")
                    {
                        Ensure(got is null, $"{label}: a successful conditional DeleteItem left an item: {Py.Repr(got)}");
                    }
                    else if (op == "Put")
                    {
                        Ensure(
                            got is not null && Py.Equal(got.GetValueOrDefault("attr"), New()) && Py.Equal(got.GetValueOrDefault("marker"), S("applied")),
                            $"{label}: a successful conditional PutItem did not land: {Py.Repr(got)}");
                    }
                    else
                    {
                        Ensure(
                            got is not null && Py.Equal(got.GetValueOrDefault("marker"), S("applied")),
                            $"{label}: a successful conditional UpdateItem did not land: {Py.Repr(got)}");
                    }
                }
                else
                {
                    if (state == "absent")
                    {
                        Ensure(got is not null && !got.ContainsKey("attr"), $"{label}: a rejected call must not have created attr: {Py.Repr(got)}");
                    }
                    else
                    {
                        var want = state == "match" ? Match() : Mismatch();
                        Ensure(got is not null && Py.Equal(got.GetValueOrDefault("attr"), want), $"{label}: a rejected call must not have changed attr: {Py.Repr(got)}");
                    }

                    Ensure(got is not null && !got.ContainsKey("marker"), $"{label}: a rejected call must not have set marker: {Py.Repr(got)}");
                }
            }
        }
    }
}
