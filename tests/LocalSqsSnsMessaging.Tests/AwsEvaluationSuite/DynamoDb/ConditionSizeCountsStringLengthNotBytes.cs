// Translated from behavioral/dynamodb/check_condition_size_counts_string_length_not_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-size-counts-string-length-not-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionSizeCountsStringLengthNotBytes
{
    /// <summary>
    /// size() on a String counts UTF-16 code units: a 3-character multi-byte BMP string has
    /// size 3 (not 7 UTF-8 bytes), and a single astral-plane character -- one code point, one
    /// surrogate pair -- has size 2, exactly so in a ConditionExpression and a Query FilterExpression.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-size-counts-string-length-not-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        // 'e' with an accent, a Greek letter and a CJK character: 3 characters, 7 UTF-8 bytes.
        const string value = "éα中";
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("bmp")), ("s", S(value))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("a")), ("sk", S("bmp")), ("s", S(value)), ("ok", N("1"))),
            ConditionExpression = "size(s) = :n",
            ExpressionAttributeValues = Item((":n", N("3"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a")), ("sk", S("bmp"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got.GetValueOrDefault("ok"), N("1")),
            $"size() must count the 3 characters rather than the 7 UTF-8 bytes, got {Py.Repr(got)}");

        // An astral-plane character is one code point and one grapheme, but UTF-16 represents it
        // as a surrogate pair, so size() reports 2 for one such character -- not 1, as a code-point
        // counter would, and not 4, as a UTF-8-byte counter would.
        const string astral = "\U0001f600";
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("astral1")), ("s", S(astral))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("a")), ("sk", S("astral1")), ("s", S(astral)), ("ok", N("1"))),
            ConditionExpression = "size(s) = :n",
            ExpressionAttributeValues = Item((":n", N("2"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a")), ("sk", S("astral1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got.GetValueOrDefault("ok"), N("1")),
            $"size() must count one astral-plane character as 2 (its UTF-16 surrogate pair), got {Py.Repr(got)}");

        // Two astral-plane characters concatenated: size() reports 4, as AWS actually measures it.
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("astral2")), ("s", S(astral + astral))));

        // A Query FilterExpression applies size() over the matched partition the same way: each
        // sort key's item is returned only under the size its own string actually measures as.
        var bySize2 = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "size(s) = :n",
            ExpressionAttributeValues = Item((":p", S("a")), (":n", N("2"))),
        })).Items;
        Ensure(
            bySize2.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal).SetEquals(["astral1"]),
            $"a FilterExpression of size(s) = 2 must match only the single astral character, got {Py.Repr(bySize2)}");
        var bySize4 = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "size(s) = :n",
            ExpressionAttributeValues = Item((":p", S("a")), (":n", N("4"))),
        })).Items;
        Ensure(
            bySize4.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal).SetEquals(["astral2"]),
            $"a FilterExpression of size(s) = 4 must match only the doubled astral character, got {Py.Repr(bySize4)}");
    }
}
