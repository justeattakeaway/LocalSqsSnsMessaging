// Translated from behavioral/dynamodb/check_scan_filter_expression_le_and_lt_on_numeric_decimals_and_negatives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-le-and-lt-on-numeric-decimals-and-negatives</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionLeAndLtOnNumericDecimalsAndNegatives
{
    private const string Check = "scan-filter-expression-le-and-lt-on-numeric-decimals-and-negatives";

    /// <summary>
    /// Five rows with a decimal and negative N column <c>amount</c>. Every expected set below is derived
    /// from these numeric values, compared numerically rather than as strings.
    /// </summary>
    private static readonly (string Id, string Amount)[] Rows =
    [
        ("r1", "-5"),
        ("r2", "-1.5"),
        ("r3", "0.25"),
        ("r4", "3.75"),
        ("r5", "10"),
    ];

    /// <summary>A fresh table under a minted name, seeded with the five rows via one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbfelt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var items = Rows
            .Select(r => new WriteRequest { PutRequest = new PutRequest { Item = Item(("id", S(r.Id)), ("amount", N(r.Amount))) } })
            .ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>> { [name] = items },
        });
        Ensure(
            (resp.UnprocessedItems ?? []).Count == 0,
            $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }

    private static double Float(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>
    /// A Scan FilterExpression's &lt;= and &lt; on an N attribute compare decimals and negatives
    /// numerically, and each matches the legacy ScanFilter twin's set exactly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-le-and-lt-on-numeric-decimals-and-negatives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);

        async Task<SortedSet<string>> ScanIds(ScanRequest request)
        {
            request.TableName = name;
            request.ConsistentRead = true;
            var resp = await ddb.ScanAsync(request);
            return new SortedSet<string>(resp.Items.Select(i => i["id"].S), StringComparer.Ordinal);
        }

        static Dictionary<string, Condition> Legacy(ComparisonOperator op, AttributeValue value) => new()
        {
            ["amount"] = new() { ComparisonOperator = op, AttributeValueList = [value] },
        };

        // <= on a decimal bound: amount <= 0.25 selects the two negatives plus the tied decimal.
        var leExpected = new SortedSet<string>(Rows.Where(r => Float(r.Amount) <= 0.25).Select(r => r.Id), StringComparer.Ordinal);
        var feLe = await ScanIds(new ScanRequest
        {
            FilterExpression = "amount <= :v",
            ExpressionAttributeValues = Item((":v", N("0.25"))),
        });
        Ensure(feLe.SetEquals(leExpected), $"FilterExpression 'amount <= 0.25' returned {Py.Repr(feLe)}, expected {Py.Repr(leExpected)}");
        var legacyLe = await ScanIds(new ScanRequest { ScanFilter = Legacy(ComparisonOperator.LE, N("0.25")) });
        Ensure(
            legacyLe.SetEquals(feLe),
            $"legacy ScanFilter LE on amount returned {Py.Repr(legacyLe)}, must match FilterExpression's " +
            $"{Py.Repr(feLe)}");

        // < on the same decimal bound: amount < 0.25 excludes the tied value, leaving only the negatives.
        var ltExpected = new SortedSet<string>(Rows.Where(r => Float(r.Amount) < 0.25).Select(r => r.Id), StringComparer.Ordinal);
        var feLt = await ScanIds(new ScanRequest
        {
            FilterExpression = "amount < :v",
            ExpressionAttributeValues = Item((":v", N("0.25"))),
        });
        Ensure(feLt.SetEquals(ltExpected), $"FilterExpression 'amount < 0.25' returned {Py.Repr(feLt)}, expected {Py.Repr(ltExpected)}");
        var legacyLt = await ScanIds(new ScanRequest { ScanFilter = Legacy(ComparisonOperator.LT, N("0.25")) });
        Ensure(
            legacyLt.SetEquals(feLt),
            $"legacy ScanFilter LT on amount returned {Py.Repr(legacyLt)}, must match FilterExpression's " +
            $"{Py.Repr(feLt)}");
    }
}
