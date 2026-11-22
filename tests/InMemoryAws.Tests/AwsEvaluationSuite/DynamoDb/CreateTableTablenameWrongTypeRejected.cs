// Translated from behavioral/dynamodb/check_create_table_tablename_wrong_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-tablename-wrong-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableTablenameWrongTypeRejected
{
    private static string? Member(JsonElement? body, string name) =>
        body is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// CreateTable refuses a TableName sent as a JSON number with a serializer error, while
    /// a correctly typed TableName over the same signed route still creates, reads back and deletes.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_CreateTable.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-tablename-wrong-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ctname-");

        Task<RawDynamoDbResponse> Raw(object body) => ctx.DynamoDbRaw("CreateTable", JsonSerializer.Serialize(body));

        // ---- the malformed call: TableName sent as a JSON number, not a string.
        var resp = await Raw(new Dictionary<string, object>
        {
            ["TableName"] = 1234567890,
            ["AttributeDefinitions"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["AttributeType"] = "S" } },
            ["KeySchema"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["KeyType"] = "HASH" } },
            ["BillingMode"] = "PAY_PER_REQUEST",
        });
        var status = (int)resp.StatusCode;
        Ensure(status == 400, $"CreateTable with a numeric TableName gave status {status}: {resp.Body}");
        Ensure(
            resp.Type == "com.amazon.coral.service#SerializationException",
            $"CreateTable with a numeric TableName gave __type {Py.Repr(resp.Type)}, expected "
            + "'com.amazon.coral.service#SerializationException'");
        var message = Member(resp.Json, "Message");
        Ensure(
            message == "NUMBER_VALUE cannot be converted to String",
            $"CreateTable with a numeric TableName gave message {Py.Repr(message)}, expected "
            + "'NUMBER_VALUE cannot be converted to String'");

        // ---- positive control, over the same signed route: a correctly typed TableName still works.
        resp = await Raw(new Dictionary<string, object>
        {
            ["TableName"] = table,
            ["AttributeDefinitions"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["AttributeType"] = "S" } },
            ["KeySchema"] = new[] { new Dictionary<string, string> { ["AttributeName"] = "pk", ["KeyType"] = "HASH" } },
            ["BillingMode"] = "PAY_PER_REQUEST",
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        status = (int)resp.StatusCode;
        Ensure(status == 200, $"CreateTable with a string TableName gave status {status}: {resp.Body}");
        var echoed = resp.Json!.Value.GetProperty("TableDescription").GetProperty("TableName").GetString();
        Ensure(echoed == table, $"CreateTable echoed TableName {Py.Repr(echoed)}, expected {Py.Repr(table)}");

        var clock = FidelityTime.StartNew();
        string? statusNow = null;
        while (clock.Elapsed.TotalSeconds < 300.0)
        {
            statusNow = (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
            if (statusNow == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(statusNow == "ACTIVE", $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(statusNow)})");
        var desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.TableName == table, $"describe named {Py.Repr(desc.TableName)}, expected {Py.Repr(table)}");

        await ddb.DeleteTableAsync(table);
        clock = FidelityTime.StartNew();
        string? gone = null;
        while (clock.Elapsed.TotalSeconds < 300.0)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                gone = error.Code;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(gone == "ResourceNotFoundException", $"table {Py.Repr(table)} still describable after delete_table (code {Py.Repr(gone)})");
    }
}
