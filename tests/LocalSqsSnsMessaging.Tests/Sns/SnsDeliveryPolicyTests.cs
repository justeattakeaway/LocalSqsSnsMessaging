using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Sns;

public class SnsDeliveryPolicyTests
{
    [Test]
    public void Default_IsThreeRetriesTwentySecondsApart()
    {
        var policy = SnsDeliveryPolicy.Resolve(null, null);

        policy.GetRetryDelays().ShouldBe([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)]);
        policy.ContentType.ShouldBe("text/plain; charset=UTF-8");
    }

    [Test]
    public void TopicPolicy_UsesTheTopicShape_AndSubscriptionOverridesIt()
    {
        const string topic = """
            {"http": {
                "defaultHealthyRetryPolicy": {"numRetries": 10, "minDelayTarget": 3, "maxDelayTarget": 3},
                "defaultRequestPolicy": {"headerContentType": "application/json"}
            }}
            """;
        const string subscription = """{"healthyRetryPolicy": {"numRetries": 1}}""";

        var topicOnly = SnsDeliveryPolicy.Resolve(null, topic);
        topicOnly.GetRetryDelays().Count.ShouldBe(10);
        topicOnly.GetRetryDelays()[0].ShouldBe(TimeSpan.FromSeconds(3));
        topicOnly.ContentType.ShouldBe("application/json");

        // The subscription overrides only what it specifies; the rest comes from the topic.
        var merged = SnsDeliveryPolicy.Resolve(subscription, topic);
        merged.GetRetryDelays().ShouldBe([TimeSpan.FromSeconds(3)]);
        merged.ContentType.ShouldBe("application/json");
    }

    [Test]
    public void TopicPolicy_CanDisableSubscriptionOverrides()
    {
        const string topic = """{"http": {"defaultHealthyRetryPolicy": {"numRetries": 10}, "disableSubscriptionOverrides": true}}""";
        const string subscription = """{"healthyRetryPolicy": {"numRetries": 1}}""";

        SnsDeliveryPolicy.Resolve(subscription, topic).GetRetryDelays().Count.ShouldBe(10);
    }

    [Test]
    public void SubscriptionPolicyAlone_IsAppliedOverDefaults()
    {
        const string subscription = """{"healthyRetryPolicy": {"numRetries": 1, "minDelayTarget": 2, "maxDelayTarget": 2}}""";

        SnsDeliveryPolicy.Resolve(subscription, null).GetRetryDelays().ShouldBe([TimeSpan.FromSeconds(2)]);
    }

    [Test]
    public void Phases_AreLaidOutInOrder()
    {
        const string json = """
            {"healthyRetryPolicy": {
                "numRetries": 7,
                "numNoDelayRetries": 1,
                "numMinDelayRetries": 1,
                "numMaxDelayRetries": 2,
                "minDelayTarget": 10,
                "maxDelayTarget": 40,
                "backoffFunction": "linear"
            }}
            """;

        var delays = SnsDeliveryPolicy.Resolve(json, null).GetRetryDelays();

        // 1 immediate, 1 at min, 3 backoff (min -> max), 2 at max.
        delays.ShouldBe([
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(40)
        ]);
    }

    [Test]
    public void Exponential_ReachesMaximumFasterThanLinear()
    {
        const string linear = """{"healthyRetryPolicy": {"numRetries": 5, "minDelayTarget": 1, "maxDelayTarget": 100, "backoffFunction": "linear"}}""";
        const string exponential = """{"healthyRetryPolicy": {"numRetries": 5, "minDelayTarget": 1, "maxDelayTarget": 100, "backoffFunction": "exponential"}}""";

        var linearDelays = SnsDeliveryPolicy.Resolve(linear, null).GetRetryDelays();
        var exponentialDelays = SnsDeliveryPolicy.Resolve(exponential, null).GetRetryDelays();

        linearDelays[0].ShouldBe(TimeSpan.FromSeconds(1));
        linearDelays[^1].ShouldBe(TimeSpan.FromSeconds(100));
        exponentialDelays[0].ShouldBe(TimeSpan.FromSeconds(1));
        exponentialDelays[^1].ShouldBe(TimeSpan.FromSeconds(100));
        exponentialDelays[2].ShouldBeGreaterThan(linearDelays[2]);
    }

    [Test]
    public void RequestPolicy_SetsContentType()
    {
        SnsDeliveryPolicy.Resolve("""{"requestPolicy": {"headerContentType": "application/json"}}""", null)
            .ContentType.ShouldBe("application/json");
    }
}
