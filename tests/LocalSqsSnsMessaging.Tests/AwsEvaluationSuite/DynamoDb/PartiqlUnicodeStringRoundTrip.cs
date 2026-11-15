// Translated from behavioral/dynamodb/check_partiql_unicode_string_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-unicode-string-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUnicodeStringRoundTrip
{
    private const string CheckId = "partiql-unicode-string-round-trip";

    /// <summary>
    /// A unicode string value inserted via PartiQL INSERT is returned unchanged by a
    /// subsequent PartiQL SELECT.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-unicode-string-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);

        var pk = FidelityContext.UniqueName("item-");
        const string text = "日本語 مرحبا \U0001F680 café";

        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'text': ?}}",
            Parameters = [S(pk), S(text)],
        });

        var resp = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT text FROM \"{table}\" WHERE pk = ?",
            Parameters = [S(pk)],
        });
        var items = resp.Items;
        Ensure(items.Count == 1, $"expected exactly one PartiQL result, got {items.Count}");
        Ensure(items[0]["text"].S == text, $"unicode text mismatch: {Py.Repr(items[0]["text"].S)} != {Py.Repr(text)}");
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
