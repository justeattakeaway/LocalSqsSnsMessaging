// Translated from behavioral/dynamodb/check_scan_filter_in_operator_binary_operand_exact_byte_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-in-operator-binary-operand-exact-byte-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterInOperatorBinaryOperandExactByteMatch
{
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Table seeded with Binary-typed blob attributes sharing byte prefixes.</summary>
    private static async Task<string> SeedBlobTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        (string Pk, byte[] Blob)[] rows =
        [
            ("u1", [0x00, 0x01, 0x02]),
            ("u2", [0x00, 0x01, 0x03]),
            ("u3", [0x00, 0x01, 0x02, 0x00]),
            ("u4", [0xff, 0xfe]),
        ];
        foreach (var (pk, blob) in rows)
        {
            await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("blob", B(blob))));
        }

        return t;
    }

    /// <summary>
    /// FilterExpression 'IN (...)' on a Binary operand matches by exact byte sequence,
    /// not by a shared byte prefix or a trailing null byte.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-in-operator-binary-operand-exact-byte-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await SeedBlobTable(ctx);

        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "#b IN (:v1, :v2)",
            ExpressionAttributeNames = Names(("#b", "blob")),
            ExpressionAttributeValues = Item((":v1", B([0x00, 0x01, 0x02])), (":v2", B([0xff, 0xfe]))),
            ConsistentRead = true,
        });
        var pks = new SortedSet<string>(resp.Items.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(resp.Count == 2, $"expected Count=2 for blob IN (00 01 02, ff fe), got {resp.Count}");
        Ensure(pks.SetEquals(["u1", "u4"]), $"expected pks {{'u1', 'u4'}} for blob IN (00 01 02, ff fe), got {Py.Repr(pks)}");

        var respPadded = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "#b IN (:v3)",
            ExpressionAttributeNames = Names(("#b", "blob")),
            ExpressionAttributeValues = Item((":v3", B([0x00, 0x01, 0x02, 0x00]))),
            ConsistentRead = true,
        });
        var paddedPks = new SortedSet<string>(respPadded.Items.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(respPadded.Count == 1, $"expected Count=1 for blob IN (00 01 02 00), got {respPadded.Count}");
        Ensure(paddedPks.SetEquals(["u3"]), $"expected the trailing-null-extended value to match only u3, got {Py.Repr(paddedPks)}");
    }
}
