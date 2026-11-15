// Translated from behavioral/dynamodb/check_partiql_select_between_in_or_order_by_desc_and_not_equal.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-between-in-or-order-by-desc-and-not-equal</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectBetweenInOrOrderByDescAndNotEqual
{
    private const string CheckId = "partiql-select-between-in-or-order-by-desc-and-not-equal";

    // (sort key, hue, fit) in partition 'A'; null means the attribute is absent from the item.
    private static readonly (int Sk, string? Hue, string? Fit)[] RowsA =
    [
        (1, "red", null),     // matches the filter, but sits below BETWEEN's lower bound
        (2, "red", null),     // lower bound itself: BETWEEN is inclusive
        (3, "green", "L"),    // only the OR's right arm matches
        (4, "green", "S"),    // neither arm matches
        (5, null, "L"),       // hue absent; the OR's right arm matches
        (6, "blue", null),    // upper bound itself
        (7, "blue", null),    // matches the filter, but sits above BETWEEN's upper bound
    ];

    // A sibling partition holding an item that would match every non-key predicate.
    private static readonly (int Sk, string? Hue, string? Fit)[] RowsB = [(3, "red", "L")];

    private static Dictionary<string, AttributeValue> MakeItem(string pk, int sk, string? hue, string? fit)
    {
        var item = Item(("pk", S(pk)), ("sk", N(sk.ToString(CultureInfo.InvariantCulture))));
        if (hue is not null)
        {
            item["hue"] = S(hue);
        }

        if (fit is not null)
        {
            item["fit"] = S(fit);
        }

        return item;
    }

    /// <summary>Every item the statement returns, following NextToken (IN results page at 10 items).</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> SelectAll(IAmazonDynamoDB ddb, string statement)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var request = new ExecuteStatementRequest { Statement = statement, ConsistentRead = true };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await ddb.ExecuteStatementAsync(request);
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return items;
            }
        }
    }

    /// <summary>
    /// A SELECT combining pk equality, sort-key BETWEEN, IN and OR returns exactly the matching items in
    /// ORDER BY ... DESC order, and a &lt;&gt; filter also keeps an item that lacks the attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-between-in-or-order-by-desc-and-not-equal")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlsel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        foreach (var (sk, hue, fit) in RowsA)
        {
            await ddb.PutItemAsync(table, MakeItem("A", sk, hue, fit));
        }

        foreach (var (sk, hue, fit) in RowsB)
        {
            await ddb.PutItemAsync(table, MakeItem("B", sk, hue, fit));
        }

        // Leg 1: pk equality AND sk BETWEEN AND (IN list OR equality), ordered by sk descending.
        var combined = await SelectAll(
            ddb,
            $"SELECT * FROM \"{table}\" WHERE pk = 'A' AND sk BETWEEN 2 AND 6 AND (hue IN ['red', 'blue'] OR fit = 'L') ORDER BY sk DESC");
        var want = RowsA.OrderByDescending(r => r.Sk)
            .Where(r => r.Sk is >= 2 and <= 6 && (r.Hue is "red" or "blue" || r.Fit == "L"))
            .Select(r => MakeItem("A", r.Sk, r.Hue, r.Fit))
            .ToList();
        var gotKeys = combined.Select(i => (i["pk"].S, i["sk"].N)).ToList();
        Ensure(want.Select(i => i["sk"].N).SequenceEqual(["6", "5", "3", "2"]), "fixture drifted from the claim");
        Ensure(
            gotKeys.SequenceEqual([("A", "6"), ("A", "5"), ("A", "3"), ("A", "2")]),
            $"pk = A AND sk BETWEEN 2 AND 6 AND (hue IN [red, blue] OR fit = L) ORDER BY sk DESC must return sort keys 6, 5, 3, 2 in that order (both bounds inclusive, each OR arm alone admitting an item, nothing from partition B), got {Py.Repr(gotKeys)}");
        Ensure(
            Py.Equal(combined, want),
            $"the selected items must come back whole and unaltered.\nwant: {Py.Repr(want)}\ngot:  {Py.Repr(combined)}");

        // Leg 2: the same predicate without ORDER BY comes back in ascending sort-key order, so the
        // DESC above is a reversal the engine performed, not the natural order.
        var natural = await SelectAll(
            ddb,
            $"SELECT * FROM \"{table}\" WHERE pk = 'A' AND sk BETWEEN 2 AND 6 AND (hue IN ['red', 'blue'] OR fit = 'L')");
        var naturalKeys = natural.Select(i => i["sk"].N).ToList();
        Ensure(
            naturalKeys.SequenceEqual(["2", "3", "5", "6"]),
            $"without ORDER BY a single-partition SELECT returns ascending sort-key order; got {Py.Repr(naturalKeys)}");

        // Leg 3: <> keeps every item whose hue is not red. Item sk=5 has no hue at all, and real AWS
        // returns it: a missing attribute is "not equal" to the literal, as in a DynamoDB
        // condition or filter expression.
        var notRed = await SelectAll(ddb, $"SELECT * FROM \"{table}\" WHERE pk = 'A' AND hue <> 'red'");
        var notRedKeys = notRed.Select(i => i["sk"].N).ToList();
        Ensure(
            notRedKeys.SequenceEqual(["3", "4", "5", "6", "7"]),
            $"pk = A AND hue <> 'red' must return, in ascending sort-key order, the items whose hue is not red (3, 4, 6, 7) and the item stored without hue (5), and filter the red ones (1, 2); got {Py.Repr(notRedKeys)}");
        var wantNotRed = RowsA.Where(r => r.Hue != "red").Select(r => MakeItem("A", r.Sk, r.Hue, r.Fit)).ToList();
        Ensure(
            Py.Equal(notRed, wantNotRed),
            $"the <> result must be the whole, unaltered items.\nwant: {Py.Repr(wantNotRed)}\ngot:  {Py.Repr(notRed)}");

        // Leg 4: IS MISSING isolates exactly the hue-less item, so <> admitted sk=5 as an item with
        // no hue, not because it holds some other hue value.
        var missing = await SelectAll(ddb, $"SELECT * FROM \"{table}\" WHERE pk = 'A' AND hue IS MISSING");
        var missingKeys = missing.Select(i => i["sk"].N).ToList();
        Ensure(
            missingKeys.SequenceEqual(["5"]),
            $"pk = A AND hue IS MISSING must return exactly the item stored without hue (5), got {Py.Repr(missingKeys)}");
    }
}
