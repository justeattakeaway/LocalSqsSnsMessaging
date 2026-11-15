// Translated from behavioral/dynamodb/check_put_get_numeric_high_precision_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-numeric-high-precision-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetNumericHighPrecisionRoundTrip
{
    private const string CheckId = "put-get-numeric-high-precision-round-trip";

    /// <summary>
    /// A Number attribute with 38 significant digits and a negative exponent survives
    /// PutItem/GetItem exactly, without float rounding.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-get-numeric-high-precision-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);
        // 38 significant digits, value well below 1 (i.e. a large negative exponent in
        // normalized scientific form): 0.<20 zeros><38 digits>
        const string significantDigits = "12345678901234567890123456789012345678";
        Ensure(significantDigits.Length == 38, $"test literal must carry 38 significant digits, got {significantDigits.Length}");
        var numStr = "0." + new string('0', 20) + significantDigits;
        var pk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("num", N(numStr))));
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
        var item = resp.Item;
        Ensure(item is not null, "item not found after PutItem");
        var returned = item["num"].N;
        var expected = ExactDecimal(numStr);
        var actual = ExactDecimal(returned);
        Ensure(actual == expected, $"numeric precision lost: {returned} != {numStr}");
    }

    /// <summary>Python's <c>Decimal(text)</c> for comparison: the exact value, normalized to (mantissa, exponent).</summary>
    private static (BigInteger Mantissa, int Exponent) ExactDecimal(string text)
    {
        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }

        var mantissa = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        if (mantissa.IsZero)
        {
            return (BigInteger.Zero, 0);
        }

        while (mantissa % 10 == 0)
        {
            mantissa /= 10;
            exponent++;
        }

        return (mantissa, exponent);
    }

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        return name;
    }
}
