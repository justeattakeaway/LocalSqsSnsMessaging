// Translated from behavioral/dynamodb/check_list_tags_of_resource_arn_wrong_json_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-tags-of-resource-arn-wrong-json-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTagsOfResourceArnWrongJsonTypeRejected
{
    private const string CheckId = "list-tags-of-resource-arn-wrong-json-type-rejected";
    private const string Ser = "com.amazon.coral.service#SerializationException";

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 300.0);
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= deadline && BudgetSpent(ctx))
            {
                throw new TimeoutException($"table {Py.Repr(table)} ACTIVE: the run budget ended first; not graded");
            }

            Ensure(clock.Elapsed.TotalSeconds < deadline, $"table {Py.Repr(table)} never reached ACTIVE (last {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>Delete a table, bounded-retrying only DynamoDB's own tags-still-updating refusal.</summary>
    private static async Task TeardownTable(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(table);
                return;
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (ResourceInUseException ex) when (ex.Message.Contains("tags are being updated", StringComparison.Ordinal))
            {
                Ensure(clock.Elapsed.TotalSeconds < deadline, $"table {Py.Repr(table)} tags never settled: {ex.Message}");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            }
        }
    }

    private static HashSet<(string, string)> TagSet(IEnumerable<Tag> tags) => [.. tags.Select(t => (t.Key, t.Value))];

    private static HashSet<(string, string)> TagSet(JsonElement body) =>
        body.TryGetProperty("Tags", out var tags)
            ? [.. tags.EnumerateArray().Select(t => (t.GetProperty("Key").GetString()!, t.GetProperty("Value").GetString()!))]
            : [];

    private static List<(string, string)> Sorted(IEnumerable<(string, string)> tags) =>
        [.. tags.OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2, StringComparer.Ordinal)];

    private static string? Member(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// ListTagsOfResource refuses a ResourceArn sent as a non-string JSON value with
    /// SerializationException, leaving a table's existing tags unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-tags-of-resource-arn-wrong-json-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ltrtype-t-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => TeardownTable(ctx, ddb, table));
        await AwaitTableActive(ctx, ddb, table);
        var tableArn = (await ddb.DescribeTableAsync(table)).Table.TableArn;

        HashSet<(string, string)> expectedTags = [("sim2real", CheckId), ("env", "test")];

        await ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = tableArn, Tags = [new Tag { Key = "env", Value = "test" }] });
        List<Tag> controlTags;
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            controlTags = (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })).Tags ?? [];
            if (TagSet(controlTags).SetEquals(expectedTags) || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(TagSet(controlTags).SetEquals(expectedTags), $"ListTagsOfResource did not read back the tag TagResource wrote: {Py.Repr(Sorted(TagSet(controlTags)))}");

        async Task<(int Status, JsonElement Body)> Raw(string resourceArnJson)
        {
            var resp = await ctx.DynamoDbRaw("ListTagsOfResource", $"{{\"ResourceArn\": {resourceArnJson}}}");
            return ((int)resp.StatusCode, resp.Json ?? JsonDocument.Parse("{}").RootElement);
        }

        // ---- 1. positive control over the same raw transport: a well-typed ResourceArn succeeds.
        var (status, body) = await Raw(JsonSerializer.Serialize(tableArn));
        Ensure(status == 200, $"a well-typed ResourceArn over raw HTTP gave {status}: {Py.Repr(body)}");
        Ensure(
            TagSet(body).SetEquals(expectedTags),
            $"the raw request's Tags were {Py.Repr(body.TryGetProperty("Tags", out var rawTags) ? rawTags : null)}, expected {Py.Repr(Sorted(expectedTags))}");

        // ---- 2. ResourceArn sent as a JSON number instead of a string.
        (status, body) = await Raw("123456789012");
        Ensure(status == 400, $"a numeric ResourceArn gave status {status}: {Py.Repr(body)}");
        Ensure(Member(body, "__type") == Ser, $"a numeric ResourceArn gave __type {Py.Repr(Member(body, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(Member(body, "Message") == "NUMBER_VALUE cannot be converted to String", $"a numeric ResourceArn gave message {Py.Repr(Member(body, "Message"))}");

        // ---- 3. ResourceArn sent as a JSON array instead of a string; this one carries no message.
        (status, body) = await Raw($"[{JsonSerializer.Serialize(tableArn)}]");
        Ensure(status == 400, $"a list-typed ResourceArn gave status {status}: {Py.Repr(body)}");
        Ensure(Member(body, "__type") == Ser, $"a list-typed ResourceArn gave __type {Py.Repr(Member(body, "__type"))}, expected {Py.Repr(Ser)}");
        Ensure(
            body.EnumerateObject().Count() == 1 && Member(body, "__type") == Ser,
            $"a list-typed ResourceArn gave body {Py.Repr(body)}, expected only __type with no message");

        // ---- 4. effect-absent: neither refused call changed the table's real tags.
        var after = (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })).Tags ?? [];
        Ensure(
            TagSet(after).SetEquals(expectedTags),
            $"ListTagsOfResource after the refusals returned {Py.Repr(Sorted(TagSet(after)))}, expected the unaffected {Py.Repr(Sorted(expectedTags))}");

        await ddb.UntagResourceAsync(new UntagResourceRequest { ResourceArn = tableArn, TagKeys = ["env"] });
        List<Tag> settledTags;
        clock = FidelityTime.StartNew();
        deadline = BudgetS(ctx, 60.0);
        while (true)
        {
            settledTags = (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })).Tags ?? [];
            if (TagSet(settledTags).SetEquals([("sim2real", CheckId)]) || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(TagSet(settledTags).SetEquals([("sim2real", CheckId)]), $"UntagResource did not settle before delete: {Py.Repr(Sorted(TagSet(settledTags)))}");

        await TeardownTable(ctx, ddb, table);
        clock = FidelityTime.StartNew();
        deadline = BudgetS(ctx, 300.0);
        string? gone = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                gone = error.Code;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        if (gone is null && BudgetSpent(ctx))
        {
            throw new TimeoutException($"table {Py.Repr(table)} deleted: the run budget ended first; not graded");
        }

        Ensure(gone == "ResourceNotFoundException", $"the table was still describable after delete_table (code {Py.Repr(gone)})");
    }
}
