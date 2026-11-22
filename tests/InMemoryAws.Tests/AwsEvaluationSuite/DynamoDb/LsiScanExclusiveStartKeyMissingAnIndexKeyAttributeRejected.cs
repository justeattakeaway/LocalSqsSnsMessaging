// Translated from behavioral/dynamodb/check_lsi_scan_exclusive_start_key_missing_an_index_key_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-scan-exclusive-start-key-missing-an-index-key-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiScanExclusiveStartKeyMissingAnIndexKeyAttributeRejected
{
    private const string Check = "lsi-scan-exclusive-start-key-missing-an-index-key-attribute-rejected";
    private const string Index = "lsi1";
    private const string ErrValidation = "ValidationException";

    /// <summary><c>(Code, Message)</c> for the error <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Sk(int i) => "s" + i.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// A Scan over an LSI rejects an ExclusiveStartKey missing any one of its three key
    /// attributes with ValidationException, while a complete one resumes the walk and no rejected
    /// call changes the table's contents.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-scan-exclusive-start-key-missing-an-index-key-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-lsilekneg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
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
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("sk", S(Sk(i))), ("lsk", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        var first = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index, ConsistentRead = true, Limit = 1 });
        Ensure(first.Items.Select(it => it["sk"].S).SequenceEqual(["s00"]), Py.Repr(first.Items));
        var realKey = first.LastEvaluatedKey;
        Ensure(
            realKey is not null && Py.Equal(realKey, Item(("pk", S("p0")), ("sk", S("s00")), ("lsk", N("0")))),
            $"expected the first item's three key attributes as the LastEvaluatedKey, got {Py.Repr(realKey)}");

        // Positive control: the real, complete ExclusiveStartKey resumes the walk.
        var cont = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index, ConsistentRead = true, ExclusiveStartKey = realKey });
        var contSks = cont.Items.Select(it => it["sk"].S).ToList();
        Ensure(
            contSks.SequenceEqual(Enumerable.Range(1, 4).Select(Sk)),
            $"positive control: the real ExclusiveStartKey must resume after s00, got {Py.Repr(contSks)}");

        foreach (var missing in new[] { "pk", "sk", "lsk" })
        {
            var badKey = realKey.Where(kv => kv.Key != missing).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                IndexName = Index,
                ConsistentRead = true,
                ExclusiveStartKey = badKey,
            }));
            Ensure(
                code == ErrValidation,
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            Ensure(
                message == "The provided starting key is invalid",
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        }

        // None of the three rejected calls wrote, deleted or otherwise changed the table.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var finalSks = final.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            finalSks.SequenceEqual(Enumerable.Range(0, 5).Select(Sk)),
            $"expected the original 5 items unchanged after the rejected calls, got {Py.Repr(finalSks)}");

        // A second positive control: the real key still resumes the walk after the rejections.
        var cont2 = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index, ConsistentRead = true, ExclusiveStartKey = realKey });
        Ensure(
            Py.Equal(cont2.Items, cont.Items),
            $"the real ExclusiveStartKey's continuation changed after the rejected calls: {Py.Repr(cont2.Items)} vs {Py.Repr(cont.Items)}");
    }
}
