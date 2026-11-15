// Translated from behavioral/dynamodb/check_update_item_numeric_set_precision_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-numeric-set-precision-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemNumericSetPrecisionPreserved
{
    private const string CheckId = "update-item-numeric-set-precision-preserved";

    /// <summary>
    /// A Number Set updated via ADD retains exact numeric precision for every member,
    /// including a newly-added 38-significant-digit member, after UpdateItem.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-numeric-set-precision-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);

        var pk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("nums", NS("1.1"))));

        const string bigNum = "123456789012345678901234567890123456.78"; // 38 significant digits
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "ADD nums :n",
            ExpressionAttributeValues = Item((":n", NS(bigNum))),
        });

        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
        var item = resp.Item;
        Ensure(item is not null, "item not found after UpdateItem");
        var members = item["nums"].NS;
        Ensure(members.Count == 2, $"expected 2 set members, got {members.Count}");

        // Python's Decimal(m): exact decimal values, compared numerically.
        var decimals = members.Select(Decimal).ToHashSet();
        Ensure(decimals.Contains(Decimal("1.1")), "original member lost or altered");

        var expectedBig = Decimal(bigNum);
        Ensure(decimals.Contains(expectedBig), $"high-precision member not preserved exactly; members={Py.Repr(members)}");
    }

    /// <summary>An exact decimal number as (mantissa without trailing zeros, power of ten).</summary>
    private static (BigInteger Mantissa, int Exponent) Decimal(string text)
    {
        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0)
        {
            exponent -= text.Length - point - 1;
            text = text.Remove(point, 1);
        }

        var mantissa = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
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
