// Translated from behavioral/dynamodb/check_partiql_select_lsi_keys_only_nonprojected_filter_and_in_between_key_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-lsi-keys-only-nonprojected-filter-and-in-between-key-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectLsiKeysOnlyNonprojectedFilterAndInBetweenKeyForms
{
    private const string CheckId = "partiql-select-lsi-keys-only-nonprojected-filter-and-in-between-key-forms";
    private const string Index = "lsi1";

    private static readonly (string Sk, int Lsk)[] Rows = [("s1", 1), ("s2", 2), ("s3", 3), ("s4", 4)];

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL SELECT on a KEYS_ONLY LSI with a WHERE term on a non-key attribute is a
    /// ValidationException, while IN and BETWEEN on the index's own sort key both answer as key
    /// conditions, not as refused forms.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-lsi-keys-only-nonprojected-filter-and-in-between-key-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqllsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        foreach (var (sk, lsk) in Rows)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S("P")), ("sk", S(sk)), ("lsk", N(lsk.ToString(CultureInfo.InvariantCulture))), ("body", S($"body-{sk}"))));
        }

        // Positive control: the index-key equality alone reaches the item through the index, proving
        // the WHERE below is being evaluated against the index rather than failing some other way.
        var baseline = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' AND lsk = 1",
            ConsistentRead = true,
        })).Items ?? [];
        Ensure(
            Py.Equal(baseline, new[] { Item(("pk", S("P")), ("sk", S("s1")), ("lsk", N("1"))) }),
            $"KEYS_ONLY must project only the table and index keys, got {Py.Repr(baseline)}");

        // A WHERE term on an attribute the KEYS_ONLY projection does not hold is rejected, not
        // silently served from the base table.
        var (code, message) = await ErrorCode(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' AND body = 'body-s1'",
            ConsistentRead = true,
        }));
        Ensure(
            code == "ValidationException",
            $"a WHERE term on a non-projected attribute of a KEYS_ONLY index must be refused with ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(
            message == $"One or more parameter values were invalid: Secondary index {Index} does not project one or more filter attributes: [body]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // IN on the index's own sort key, alongside the partition key equality, answers rather than
        // being refused as an illegal key-condition form.
        var inItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' AND lsk IN [1, 3]",
            ConsistentRead = true,
        })).Items ?? [];
        var inKeys = inItems.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(inKeys.SequenceEqual(["s1", "s3"]), $"lsk IN [1, 3] must answer with sk s1 and s3, got {Py.Repr(inKeys)}");

        // BETWEEN on the index's own sort key likewise answers.
        var betweenItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' AND lsk BETWEEN 2 AND 3",
            ConsistentRead = true,
        })).Items ?? [];
        var betweenKeys = betweenItems.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(betweenKeys.SequenceEqual(["s2", "s3"]), $"lsk BETWEEN 2 AND 3 must answer with sk s2 and s3, got {Py.Repr(betweenKeys)}");

        // The rejected SELECT above is read-only: every base-table item is exactly as written.
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("P")), ("sk", S("s1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(kept, Item(("pk", S("P")), ("sk", S("s1")), ("lsk", N("1")), ("body", S("body-s1")))),
            $"the rejected SELECT must not have written anything; item now reads {Py.Repr(kept)}");
    }
}
