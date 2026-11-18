// Translated from behavioral/dynamodb/check_batch_execute_statement_malformed_slot_siblings_execute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-malformed-slot-siblings-execute</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementMalformedSlotSiblingsExecute
{
    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>
    /// BatchExecuteStatement carries a malformed statement's failure as that slot's
    /// BatchStatementResponse.Error, and the well-formed sibling statements in the same
    /// call still execute.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-malformed-slot-siblings-execute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-batch-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"INSRT INTO \"{tableName}\" VALUE {{'pk':'malformed','sk':'s1'}}" },
                new BatchStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk':'sibling-a','sk':'s1'}}" },
                new BatchStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk':'sibling-b','sk':'s1'}}" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var malformed = responses[0];
        Ensure(
            malformed.Item is null && malformed.TableName is null,
            $"expected the malformed slot to carry no effect, got {Py.Repr(Slot(malformed))}");
        var error = malformed.Error;
        Ensure(
            error is not null,
            $"expected the malformed slot to carry a per-slot Error (not abort the whole batch), got {Py.Repr(Slot(malformed))}");
        Ensure(error.Code?.Value == "ValidationError", $"expected Code ValidationError, got {Py.Repr(error.Code)}");
        const string expectedMessage = "Statement wasn't well formed, can't be processed: Unexpected token after expression";
        Ensure(error.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(error.Message)}");
        var unwritten = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Item(("pk", S("malformed")), ("sk", S("s1"))),
            ConsistentRead = true,
        });
        Ensure(unwritten.Item is null, $"malformed statement must not have written, got {Py.Repr(unwritten.Item)}");

        // Positive control: the well-formed siblings in the SAME batch call executed.
        foreach (var (slot, pk) in new[] { (responses[1], "sibling-a"), (responses[2], "sibling-b") })
        {
            Ensure(slot.Error is null, $"expected sibling {Py.Repr(pk)} to succeed, got {Py.Repr(Slot(slot))}");
            var written = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = tableName,
                Key = Item(("pk", S(pk)), ("sk", S("s1"))),
                ConsistentRead = true,
            });
            Ensure(written.Item is not null, $"expected sibling {Py.Repr(pk)} to have been written, got {Py.Repr(written.Item)}");
        }
    }

    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s (last={status})");
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
