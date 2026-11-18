// Translated from behavioral/dynamodb/check_legacy_expected_list_map_value_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-list-map-value-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedListMapValueEquality
{
    private const string CheckId = "legacy-expected-list-map-value-equality";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private const string ErrCcfeMessage = "The conditional request failed";
    private static readonly string[] States = ["match", "mismatch"];

    private static AttributeValue ListValue() => L(S("x"), N("1"));

    private static AttributeValue ListOther() => L(S("x"), N("2"));

    private static AttributeValue MapValue() => M(("a", S("y")), ("b", N("1")));

    private static AttributeValue MapOther() => M(("a", S("y")), ("b", N("2")));

    // For each document kind: the condition's reference value, and a stored value that differs from
    // it only in one nested element.
    private static readonly (string Name, Func<AttributeValue> Ref, Func<AttributeValue> Other)[] DocKinds =
    [
        ("list", ListValue, ListOther),
        ("map", MapValue, MapOther),
    ];

    // For each legacy Expected form: how to build the condition dict for a given reference value.
    private static readonly (string Name, Func<AttributeValue, ExpectedAttributeValue> Build)[] Forms =
    [
        ("value", r => new ExpectedAttributeValue { Value = r }),
        ("comparator", r => new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [r] }),
    ];

    /// <summary>
    /// Run one Expected-guarded PutItem that rewrites doc to refValue and sets marker;
    /// return ('ok', None, None) or ('error', code, msg).
    /// </summary>
    private static async Task<(string Kind, string? Code, string? Message)> Apply(IAmazonDynamoDB ddb, string table, string pk, AttributeValue refValue, ExpectedAttributeValue condition)
    {
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(pk)), ("doc", refValue), ("marker", S("applied"))),
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["doc"] = condition },
        }));
        return error is null ? ("ok", null, null) : ("error", error.Code, error.Message);
    }

    /// <summary>
    /// Legacy Expected equality (Value, and ComparisonOperator EQ) on a List or Map
    /// AttributeValue is graded by deep structural equality on PutItem.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-list-map-value-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-expdoc-");
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

        foreach (var (kindName, refValue, otherValue) in DocKinds)
        {
            foreach (var (formName, buildCondition) in Forms)
            {
                foreach (var state in States)
                {
                    var pk = $"{kindName}-{formName}-{state}";
                    var stored = state == "match" ? refValue() : otherValue();
                    await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("doc", stored)));
                    var label = $"{kindName}/{formName}/{state}";

                    var (kind, code, message) = await Apply(ddb, table, pk, refValue(), buildCondition(refValue()));

                    if (state == "match")
                    {
                        Ensure(kind == "ok", $"{label}: expected the deep-equal {kindName} to satisfy the legacy Expected condition, got {code ?? "None"}: {Py.Repr(message)}");
                    }
                    else
                    {
                        Ensure(kind == "error", $"{label}: expected {ErrConditionalCheckFailed} for a {kindName} that differs by a nested element, call succeeded");
                        Ensure(code == ErrConditionalCheckFailed, $"{label}: expected {ErrConditionalCheckFailed}, got {code}: {Py.Repr(message)}");
                        Ensure(message == ErrCcfeMessage, $"{label}: unexpected message {Py.Repr(message)}");
                    }

                    var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
                    if (state == "match")
                    {
                        Ensure(
                            got is not null && Py.Equal(got.GetValueOrDefault("doc"), refValue()) && Py.Equal(got.GetValueOrDefault("marker"), S("applied")),
                            $"{label}: a successful conditional PutItem did not land: {Py.Repr(got)}");
                    }
                    else
                    {
                        Ensure(
                            got is not null && Py.Equal(got.GetValueOrDefault("doc"), otherValue()) && !got.ContainsKey("marker"),
                            $"{label}: a rejected conditional PutItem must leave doc unchanged: {Py.Repr(got)}");
                    }
                }
            }
        }
    }
}
