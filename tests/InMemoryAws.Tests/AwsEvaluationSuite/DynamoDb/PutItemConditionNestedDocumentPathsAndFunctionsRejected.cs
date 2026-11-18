// Translated from behavioral/dynamodb/check_put_item_condition_nested_document_paths_and_functions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-nested-document-paths-and-functions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionNestedDocumentPathsAndFunctionsRejected
{
    private const string CheckId = "put-item-condition-nested-document-paths-and-functions-rejected";
    private static readonly (string? Code, string? Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>The (Code, Message) fn() raises. Raises when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> a, params (string Name, AttributeValue Value)[] more)
    {
        var item = new Dictionary<string, AttributeValue>(a, StringComparer.Ordinal);
        foreach (var (name, value) in more)
        {
            item[name] = value;
        }

        return item;
    }

    /// <summary>
    /// PutItem's ConditionExpression evaluating a function or document path to false against
    /// the existing item is ConditionalCheckFailedException and never replaces the item; a
    /// holding condition on the same item still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-nested-document-paths-and-functions-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-cond-neg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("ddb-put-cond-neg")));
        var original = With(
            key,
            ("m", M(("k", S("inner")), ("n", L(N("10"), S("ten"))))),
            ("tag", S("alpha-needle-beta")));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        async Task Rejected(string condExpr, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
        {
            var request = new PutItemRequest
            {
                TableName = table,
                Item = With(key, ("overwritten", Bool(true))),
                ConditionExpression = condExpr,
            };
            if (names.Count > 0)
            {
                request.ExpressionAttributeNames = names;
            }

            if (values.Count > 0)
            {
                request.ExpressionAttributeValues = values;
            }

            var (code, msg) = await Error(async () => await ddb.PutItemAsync(request));
            Ensure((code, msg) == Ccf, $"PutItem under {Py.Repr(condExpr)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var got = await Read();
            Ensure(Py.Equal(got, original), $"PutItem under failing {Py.Repr(condExpr)} left {Py.Repr(got)}, expected {Py.Repr(original)}");
        }

        // attribute_exists on a nested map path that is absent.
        await Rejected("attribute_exists(#m.#z)", Names(("#m", "m"), ("#z", "z")), Item());

        // attribute_not_exists on a nested map path that is present.
        await Rejected("attribute_not_exists(#m.#k)", Names(("#m", "m"), ("#k", "k")), Item());

        // Compound AND: the second arm is false.
        await Rejected(
            "#m.#k = :inner AND #m.#n[0] = :wrong",
            Names(("#m", "m"), ("#k", "k"), ("#n", "n")),
            Item((":inner", S("inner")), (":wrong", N("11"))));

        // OR with both arms false.
        await Rejected(
            "#m.#k = :a OR #m.#k = :b",
            Names(("#m", "m"), ("#k", "k")),
            Item((":a", S("a")), (":b", S("b"))));

        // NOT negates a true operand to false.
        await Rejected("NOT (#m.#k = :inner)", Names(("#m", "m"), ("#k", "k")), Item((":inner", S("inner"))));

        // contains(attr, substring) where the substring is absent.
        await Rejected("contains(#tag, :missing)", Names(("#tag", "tag")), Item((":missing", S("missing"))));

        // A nested path value compared against a different type: the comparison is false, not an error.
        await Rejected("#m.#k = :num", Names(("#m", "m"), ("#k", "k")), Item((":num", N("5"))));

        // Positive control: the same mechanism applies the write once the condition holds.
        var replacement = With(key, ("replaced", Bool(true)));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = replacement,
            ConditionExpression = "attribute_exists(#m.#k)",
            ExpressionAttributeNames = Names(("#m", "m"), ("#k", "k")),
        });
        var final = await Read();
        Ensure(Py.Equal(final, replacement), $"the holding control left {Py.Repr(final)}, expected {Py.Repr(replacement)}");
    }
}
