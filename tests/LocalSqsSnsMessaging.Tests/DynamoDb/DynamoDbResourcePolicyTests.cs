using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.DynamoDb;

/// <summary>
/// A table's resource policy, as DynamoDB enforces its explicit Deny statements: every request acts
/// as the bus's account root.
/// </summary>
public sealed class DynamoDbResourcePolicyTests
{
    private const string Table = "orders";

    private static readonly string[] IndexReads = ["dynamodb:Query", "dynamodb:Scan"];

    private static async Task<(AmazonDynamoDBClient Client, string TableArn, string CallerArn)> CreateTableAsync(InMemoryAwsBus bus)
    {
        var client = bus.CreateDynamoDbClient();
        var table = (await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = Table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("status", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-status",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        })).TableDescription;
        await client.PutItemAsync(Table, Item("first"));
        return (client, table.TableArn, $"arn:aws:iam::{bus.CurrentAccountId}:root");
    }

    private static Dictionary<string, AttributeValue> Item(string pk) =>
        new() { ["pk"] = new AttributeValue(pk), ["status"] = new AttributeValue("open") };

    private static Dictionary<string, AttributeValue> Key(string pk) => new() { ["pk"] = new AttributeValue(pk) };

    private static Task<PutResourcePolicyResponse> PutPolicyAsync(AmazonDynamoDBClient client, string tableArn, params object[] statements) =>
        client.PutResourcePolicyAsync(new PutResourcePolicyRequest
        {
            ResourceArn = tableArn,
            Policy = JsonSerializer.Serialize(new Dictionary<string, object> { ["Version"] = "2012-10-17", ["Statement"] = statements }),
        });

    private static Dictionary<string, object> Deny(object principal, object action, object resource) => new()
    {
        ["Effect"] = "Deny",
        ["Principal"] = new Dictionary<string, object> { ["AWS"] = principal },
        ["Action"] = action,
        ["Resource"] = resource,
    };

    private static QueryRequest QueryByStatus(string? indexName) => indexName is null
        ? new QueryRequest
        {
            TableName = Table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = new("first") },
        }
        : new QueryRequest
        {
            TableName = Table,
            IndexName = indexName,
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = new("open") },
        };

    [Test]
    public async Task DenyOnOneAction_RefusesOnlyThatAction()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        await PutPolicyAsync(client, tableArn, Deny(callerArn, "dynamodb:DeleteItem", tableArn));

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.DeleteItemAsync(Table, Key("first")));

        refusal.ErrorCode.ShouldBe("AccessDeniedException");
        refusal.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        refusal.Message.ShouldBe(
            $"User: {callerArn} is not authorized to perform: dynamodb:DeleteItem on resource: {tableArn} " +
            "with an explicit deny in a resource-based policy");
        (await client.GetItemAsync(Table, Key("first"))).Item.ShouldNotBeEmpty();
        await client.PutItemAsync(Table, Item("second"));
        (await client.ScanAsync(new ScanRequest { TableName = Table })).Count.ShouldBe(2);
        (await client.QueryAsync(QueryByStatus(indexName: null))).Count.ShouldBe(1);
        await client.DescribeTableAsync(Table);
    }

    [Test]
    public async Task DenyOnAnIndexArn_RefusesReadsOfThatIndexButNotOfTheTable()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        var indexArn = $"{tableArn}/index/by-status";
        await PutPolicyAsync(client, tableArn, Deny(callerArn, IndexReads, indexArn));

        var query = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.QueryAsync(QueryByStatus("by-status")));
        var scan = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.ScanAsync(new ScanRequest { TableName = Table, IndexName = "by-status" }));

        query.ErrorCode.ShouldBe("AccessDeniedException");
        query.Message.ShouldBe(
            $"User: {callerArn} is not authorized to perform: dynamodb:Query on resource: {indexArn} " +
            "with an explicit deny in a resource-based policy");
        scan.Message.ShouldBe(
            $"User: {callerArn} is not authorized to perform: dynamodb:Scan on resource: {indexArn} " +
            "with an explicit deny in a resource-based policy");
        (await client.QueryAsync(QueryByStatus(indexName: null))).Count.ShouldBe(1);
        (await client.ScanAsync(new ScanRequest { TableName = Table })).Count.ShouldBe(1);
    }

    [Test]
    public async Task WildcardDeny_RefusesEveryActionOnEveryResourceOfTheTable()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        await PutPolicyAsync(client, tableArn, Deny("*", "dynamodb:*", "*"));

        var get = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.GetItemAsync(Table, Key("first")));
        var put = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.PutItemAsync(Table, Item("second")));
        var query = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.QueryAsync(QueryByStatus("by-status")));

        get.Message.ShouldBe(
            $"User: {callerArn} is not authorized to perform: dynamodb:GetItem on resource: {tableArn} " +
            "with an explicit deny in a resource-based policy");
        put.ErrorCode.ShouldBe("AccessDeniedException");
        query.ErrorCode.ShouldBe("AccessDeniedException");
    }

    [Test]
    public async Task WildcardActionAndResourcePatterns_MatchAsIamGlobs()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        await PutPolicyAsync(client, tableArn, Deny(callerArn, "dynamodb:Get*", $"arn:aws:dynamodb:*:{bus.CurrentAccountId}:table/ord?rs"));

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.GetItemAsync(Table, Key("first")));

        refusal.ErrorCode.ShouldBe("AccessDeniedException");
        await client.PutItemAsync(Table, Item("second"));
    }

    [Test]
    public async Task DenyForAnotherPrincipal_HasNoEffect()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, _) = await CreateTableAsync(bus);
        using var __ = client;
        await PutPolicyAsync(client, tableArn, Deny("arn:aws:iam::210987654321:root", "dynamodb:*", "*"));

        (await client.GetItemAsync(Table, Key("first"))).Item.ShouldNotBeEmpty();
        await client.PutItemAsync(Table, Item("second"));
    }

    [Test]
    public async Task DenyConditionedOnAKeyTheLibraryDoesNotModel_IsSkipped()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        var deny = Deny(callerArn, "dynamodb:GetItem", tableArn);
        deny["Condition"] = new Dictionary<string, object>
        {
            ["ForAllValues:StringEquals"] = new Dictionary<string, object> { ["dynamodb:LeadingKeys"] = new[] { "first" } },
        };
        await PutPolicyAsync(client, tableArn, deny);

        (await client.GetItemAsync(Table, Key("first"))).Item.ShouldNotBeEmpty();
    }

    [Test]
    public async Task DeletingThePolicy_LiftsTheDeny()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        await PutPolicyAsync(client, tableArn, Deny(callerArn, "dynamodb:*", "*"));
        await Should.ThrowAsync<AmazonDynamoDBException>(() => client.GetItemAsync(Table, Key("first")));

        await client.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn });

        (await client.GetItemAsync(Table, Key("first"))).Item.ShouldNotBeEmpty();
    }

    [Test]
    public async Task DenyOnTheTable_RefusesItsTableLevelOperations()
    {
        var bus = new InMemoryAwsBus();
        var (client, tableArn, callerArn) = await CreateTableAsync(bus);
        using var _ = client;
        var indexArn = $"{tableArn}/index/by-status";
        await PutPolicyAsync(client, tableArn, Deny(callerArn, "dynamodb:*", new[] { tableArn, indexArn }));

        var operations = new (string Action, string Resource, Func<Task> Call)[]
        {
            ("DescribeTable", tableArn, () => client.DescribeTableAsync(Table)),
            ("UpdateTable", tableArn, () => client.UpdateTableAsync(new UpdateTableRequest { TableName = Table, DeletionProtectionEnabled = true })),
            ("DescribeTimeToLive", tableArn, () => client.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = Table })),
            ("UpdateTimeToLive", tableArn, () => client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = Table,
                TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
            })),
            ("TagResource", tableArn, () => client.TagResourceAsync(new TagResourceRequest { ResourceArn = tableArn, Tags = [new Tag { Key = "team", Value = "a" }] })),
            ("UntagResource", tableArn, () => client.UntagResourceAsync(new UntagResourceRequest { ResourceArn = tableArn, TagKeys = ["team"] })),
            ("ListTagsOfResource", tableArn, () => client.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })),
            ("CreateBackup", tableArn, () => client.CreateBackupAsync(new CreateBackupRequest { TableName = Table, BackupName = "nightly" })),
            ("DescribeContinuousBackups", tableArn, () => client.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = Table })),
            ("UpdateContinuousBackups", tableArn, () => client.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = Table,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            })),
            ("DescribeContributorInsights", tableArn, () => client.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = Table })),
            ("DescribeContributorInsights", indexArn, () => client.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = Table, IndexName = "by-status" })),
            ("UpdateContributorInsights", tableArn, () => client.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
            {
                TableName = Table,
                ContributorInsightsAction = ContributorInsightsAction.ENABLE,
            })),
            ("DescribeKinesisStreamingDestination", tableArn, () => client.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = Table })),
            ("DescribeTableReplicaAutoScaling", tableArn, () => client.DescribeTableReplicaAutoScalingAsync(new DescribeTableReplicaAutoScalingRequest { TableName = Table })),
            ("DeleteTable", tableArn, () => client.DeleteTableAsync(Table)),
        };

        foreach (var (action, resource, call) in operations)
        {
            var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(call, action);
            refusal.Message.ShouldBe(
                $"User: {callerArn} is not authorized to perform: dynamodb:{action} on resource: {resource} " +
                "with an explicit deny in a resource-based policy",
                action);
        }

        // The policy itself stays manageable, and the table was left as it was.
        (await client.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn })).Policy.ShouldNotBeNull();
        await client.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn });
        var table = (await client.DescribeTableAsync(Table)).Table;
        table.DeletionProtectionEnabled.ShouldBe(false);
        (await client.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })).Tags.ShouldBeEmpty();
    }
}
