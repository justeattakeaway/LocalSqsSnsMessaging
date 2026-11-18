// Translated from behavioral/dynamodb/check_put_item_condition_nested_document_paths_and_functions_that_hold.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-nested-document-paths-and-functions-that-hold</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionNestedDocumentPathsAndFunctionsThatHold
{
    private const string CheckId = "put-item-condition-nested-document-paths-and-functions-that-hold";
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

    /// <summary><c>{**key, **base_doc, "seq": {"N": seq}}</c>.</summary>
    private static Dictionary<string, AttributeValue> Doc(Dictionary<string, AttributeValue> key, int seq) =>
        Item(
            ("pk", key["pk"]),
            ("m", M(("k", S("inner")), ("n", L(N("10"), S("ten"))))),
            ("status", S("active")),
            ("tag", S("alpha-needle-beta")),
            ("seq", N(seq.ToString(CultureInfo.InvariantCulture))));

    /// <summary>
    /// PutItem's ConditionExpression evaluates functions and document paths against the
    /// existing item: each holding form lets the replacement item apply; a false one fails
    /// with ConditionalCheckFailedException and leaves the stored item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-nested-document-paths-and-functions-that-hold")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-cond-");
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

        var key = Item(("pk", S("ddb-put-cond")));
        await ddb.PutItemAsync(table, Doc(key, 0));

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        async Task PutHolding(int seq, string condExpr, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
        {
            var item = Doc(key, seq);
            var request = new PutItemRequest { TableName = table, Item = item, ConditionExpression = condExpr };
            if (names.Count > 0)
            {
                request.ExpressionAttributeNames = names;
            }

            if (values.Count > 0)
            {
                request.ExpressionAttributeValues = values;
            }

            await ddb.PutItemAsync(request);
            var got = await Read();
            Ensure(Py.Equal(got, item), $"PutItem under holding condition {Py.Repr(condExpr)} left {Py.Repr(got)}, expected {Py.Repr(item)}");
        }

        // attribute_exists on a nested map path that is present.
        await PutHolding(1, "attribute_exists(#m.#k)", Names(("#m", "m"), ("#k", "k")), Item());

        // attribute_not_exists on a nested map path that is absent.
        await PutHolding(2, "attribute_not_exists(#m.#z)", Names(("#m", "m"), ("#z", "z")), Item());

        // NOT negates its operand: NOT(false) holds.
        await PutHolding(3, "NOT (#m.#k = :other)", Names(("#m", "m"), ("#k", "k")), Item((":other", S("nope"))));

        // Compound AND: both arms true.
        await PutHolding(
            4,
            "#m.#k = :inner AND #m.#n[0] = :ten",
            Names(("#m", "m"), ("#k", "k"), ("#n", "n")),
            Item((":inner", S("inner")), (":ten", N("10"))));

        // OR with one false arm: the second arm is true.
        await PutHolding(
            5,
            "#m.#k = :other OR #m.#k = :inner",
            Names(("#m", "m"), ("#k", "k")),
            Item((":other", S("other")), (":inner", S("inner"))));

        // contains(attr, substring) on a top-level string attribute.
        await PutHolding(6, "contains(#tag, :needle)", Names(("#tag", "tag")), Item((":needle", S("needle"))));

        // A nested map path referenced with plain dot notation, with no ExpressionAttributeNames,
        // because neither "m" nor "k" is a reserved word.
        await PutHolding(7, "m.k = :inner", Names(), Item((":inner", S("inner"))));

        // A reserved word ("status") used in a condition only once aliased.
        await PutHolding(8, "#st = :active", Names(("#st", "status")), Item((":active", S("active"))));

        // A near-miss: the nested path compares to a value the item does not hold.
        var before = await Read();
        var (code, msg) = await Error(async () => await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Doc(key, 9),
            ConditionExpression = "#m.#k = :other",
            ExpressionAttributeNames = Names(("#m", "m"), ("#k", "k")),
            ExpressionAttributeValues = Item((":other", S("other"))),
        }));
        Ensure((code, msg) == Ccf, $"PutItem under a failing nested condition gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var after = await Read();
        Ensure(Py.Equal(after, before), $"PutItem under a failing nested condition left {Py.Repr(after)}, expected {Py.Repr(before)}");
    }
}
