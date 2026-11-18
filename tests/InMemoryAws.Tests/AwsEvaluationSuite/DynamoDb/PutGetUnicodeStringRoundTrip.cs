// Translated from behavioral/dynamodb/check_put_get_unicode_string_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-unicode-string-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetUnicodeStringRoundTrip
{
    private const string CheckId = "put-get-unicode-string-round-trip";

    /// <summary>Multi-script unicode string attributes round-trip byte-identical via PutItem/GetItem.</summary>
    [Test]
    [DisplayName("dynamodb.put-get-unicode-string-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);
        var pk = FidelityContext.UniqueName("item-");
        var values = new List<(string Key, string Value)>
        {
            ("emoji", "\U0001F600\U0001F389\U0001F680 rocket party"),
            ("cjk", "日本語漢字テスト"),
            ("rtl", "مرحبا بالعالم"),
            ("mixed", "Héllo Wörld Ünïcode ⚡ test"),
        };
        var item = Item(("pk", S(pk)));
        foreach (var (k, v) in values)
        {
            item[k] = S(v);
        }

        await ddb.PutItemAsync(table, item);
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
        var got = resp.Item;
        Ensure(got is not null, "item not found after PutItem");
        foreach (var (k, v) in values)
        {
            Ensure(got[k].S == v, $"attribute {k} mismatch: {Py.Repr(got[k].S)} != {Py.Repr(v)}");
        }
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
