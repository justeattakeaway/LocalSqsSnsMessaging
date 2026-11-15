// Translated from behavioral/dynamodb/check_lsi_projection_include_echoes_non_key_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-projection-include-echoes-non-key-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiProjectionIncludeEchoesNonKeyAttributes
{
    private const string CheckId = "lsi-projection-include-echoes-non-key-attributes";
    private static readonly string[] Projected = ["p1", "p2", "p3"];
    private static readonly string[] LsiNames = ["lsi_include", "lsi_all", "lsi_keys"];
    private static readonly string[] GsiNames = ["gsi_include"];

    /// <summary>
    /// DescribeTable echoes INCLUDE projections with NonKeyAttributes on LSIs and GSIs, ALL and
    /// KEYS_ONLY carry none, and a strongly consistent query of the INCLUDE LSI returns the row
    /// without an attribute outside the list.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-projection-include-echoes-non-key-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("li_inc", ScalarAttributeType.S),
                new AttributeDefinition("li_all", ScalarAttributeType.S),
                new AttributeDefinition("li_keys", ScalarAttributeType.S),
                new AttributeDefinition("gi_inc", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi_include",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li_inc", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = [.. Projected] },
                },
                new LocalSecondaryIndex
                {
                    IndexName = "lsi_all",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li_all", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new LocalSecondaryIndex
                {
                    IndexName = "lsi_keys",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li_keys", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi_include",
                    KeySchema = [new KeySchemaElement("gi_inc", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = [.. Projected] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var desc = (await ddb.DescribeTableAsync(t)).Table;
        var lsis = (desc.LocalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.Projection, StringComparer.Ordinal);
        var gsis = (desc.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.Projection, StringComparer.Ordinal);
        Ensure(lsis.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LsiNames), $"all three LSIs must be described, got {Py.Repr(lsis.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(gsis.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(GsiNames), $"the GSI must be described, got {Py.Repr(gsis.Keys.Order(StringComparer.Ordinal).ToList())}");

        foreach (var (label, proj) in new[] { ("lsi_include", lsis.GetValueOrDefault("lsi_include")), ("gsi_include", gsis.GetValueOrDefault("gsi_include")) })
        {
            Ensure(proj?.ProjectionType?.Value == "INCLUDE", $"{label} must report ProjectionType=INCLUDE, got {Py.Repr(proj?.ProjectionType)}");
            Ensure(
                (proj?.NonKeyAttributes ?? []).Order(StringComparer.Ordinal).SequenceEqual(Projected.Order(StringComparer.Ordinal)),
                $"{label} must echo the NonKeyAttributes it was created with ({Py.Repr(Projected)}), got {Py.Repr(proj?.NonKeyAttributes)}");
        }

        foreach (var (name, expectedType) in new[] { ("lsi_all", "ALL"), ("lsi_keys", "KEYS_ONLY") })
        {
            var proj = lsis[name];
            Ensure(proj?.ProjectionType?.Value == expectedType, $"{name} must report ProjectionType={expectedType}, got {Py.Repr(proj?.ProjectionType)}");
            Ensure(
                proj?.NonKeyAttributes is not { Count: > 0 },
                $"{name} projects {expectedType} and must NOT carry NonKeyAttributes, got {Py.Repr(proj?.NonKeyAttributes)}");
        }

        // The projection governs reads, not only the description: an attribute outside
        // the INCLUDE list is absent from an index query even though it is on the item.
        await ddb.PutItemAsync(t, Item(("pk", S("a")), ("sk", N("1")), ("li_inc", S("x")), ("p1", S("in")), ("outside", S("out"))));
        var rows = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "lsi_include",
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("a"))),
            ConsistentRead = true,
        })).Items ?? [];
        Ensure(rows.Count == 1, $"the INCLUDE index must return the one matching row, got {rows.Count}");
        Ensure(Py.Equal(rows[0].GetValueOrDefault("p1"), S("in")), $"a projected attribute must be returned, got {Py.Repr(rows[0].GetValueOrDefault("p1"))}");
        Ensure(!rows[0].ContainsKey("outside"), $"an attribute outside NonKeyAttributes must not be projected, got {Py.Repr(rows[0].GetValueOrDefault("outside"))}");
    }
}
