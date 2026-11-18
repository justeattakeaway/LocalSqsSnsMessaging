// Translated from behavioral/dynamodb/check_batch_execute_statement_multi_attribute_gsi_select_rejected_per_slot.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-multi-attribute-gsi-select-rejected-per-slot</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementMultiAttributeGsiSelectRejectedPerSlot
{
    private const string Check = "batch-execute-statement-multi-attribute-gsi-select-rejected-per-slot";
    private const string MultiHash = "multi-hash-index";
    private static readonly (string Code, string Message) MultiAttrError = (
        "ValidationError",
        "PartiQL statements cannot be executed on tables with multi-attribute HASH keys or " +
        "multi-attribute RANGE keys.");

    /// <summary>
    /// A BatchExecuteStatement SELECT slot addressed at a multi-attribute-HASH-key GSI reports a
    /// per-slot ValidationError naming the multi-attribute refusal, ahead of the generic rule a
    /// single-attribute index's WHERE clause would give; a sibling base-table slot still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-multi-attribute-gsi-select-rejected-per-slot")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pq-bmak-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
                new AttributeDefinition("ts", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = MultiHash,
                    KeySchema =
                    [
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var desc = await Poll(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.GlobalSecondaryIndexes.All(g => g.IndexStatus == IndexStatus.ACTIVE),
            budgetS: 600.0,
            pollS: 5.0);
        Ensure(
            desc.GlobalSecondaryIndexes[0].IndexStatus == IndexStatus.ACTIVE,
            $"expected the GSI active, got {Py.Repr(desc.GlobalSecondaryIndexes.Select(g => g.IndexStatus))}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")), ("sk", S("s1")), ("tenant", S("acme")), ("region", S("eu")), ("ts", S("01"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                // A WHERE clause naming only one of the two HASH attributes: on a single-attribute index
                // this shape is the generic "must specify the primary key" case, but the multi-attribute
                // refusal takes priority over that rule here.
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant='acme'" },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='p1' AND sk='s1'" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var bad = responses[0];
        Ensure(bad.Item is null, $"expected the multi-attribute GSI SELECT slot to carry no Item, got {Py.Repr(Slot(bad))}");
        var error = bad.Error;
        Ensure(
            error is not null,
            "expected the multi-attribute GSI SELECT to be reported as a per-slot Error " +
            $"(not a thrown exception that would have aborted the whole batch), got {Py.Repr(Slot(bad))}");
        Ensure(
            (error.Code?.Value, error.Message) == MultiAttrError,
            $"expected {Py.Repr(MultiAttrError)}, got {Py.Repr((error.Code?.Value, error.Message))}");

        // Positive control: the sibling statement, addressed at the base table in the SAME batch
        // call, is unaffected by its neighbour's rejection.
        var good = responses[1];
        Ensure(good.Error is null, $"expected the base-table SELECT to succeed, got {Py.Repr(Slot(good))}");
        Ensure(
            Py.Equal(good.Item?.GetValueOrDefault("tenant"), S("acme")),
            $"expected the base-table SELECT to return the seeded item, got {Py.Repr(Slot(good))}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises; only <c>not_found</c> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary><c>read()</c> once <c>done(value)</c>; raises naming the last value when the budget runs out.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double budgetS, double pollS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed.TotalSeconds >= budgetS)
            {
                throw Fail($"condition not met within {budgetS.ToString("0.0", CultureInfo.InvariantCulture)}s; last value {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }

    /// <summary>A statement response as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Slot(BatchStatementResponse r)
    {
        var slot = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Error is { } e)
        {
            var error = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (e.Item is not null)
            {
                error["Item"] = e.Item;
            }

            if (e.Code is not null)
            {
                error["Code"] = e.Code;
            }

            if (e.Message is not null)
            {
                error["Message"] = e.Message;
            }

            slot["Error"] = error;
        }

        if (r.TableName is not null)
        {
            slot["TableName"] = r.TableName;
        }

        if (r.Item is not null)
        {
            slot["Item"] = r.Item;
        }

        return slot;
    }
}
