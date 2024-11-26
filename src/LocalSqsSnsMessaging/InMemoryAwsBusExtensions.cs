namespace LocalSqsSnsMessaging;

/// <summary>
/// Provides extension methods for the InMemoryAwsBus class to create SQS and SNS clients.
/// </summary>
public static class InMemoryAwsBusExtensions
{
    /// <summary>
    /// Creates an in-memory SQS client associated with the specified InMemoryAwsBus instance.
    /// </summary>
    /// <param name="bus">The InMemoryAwsBus instance to associate with the SQS client.</param>
    /// <returns>An InMemorySqsClient instance connected to the provided InMemoryAwsBus.</returns>
    public static InMemorySqsClient CreateSqsClient(this InMemoryAwsBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return new InMemorySqsClient(bus);
    }

    /// <summary>
    /// Creates an in-memory SNS client associated with the specified InMemoryAwsBus instance.
    /// </summary>
    /// <param name="bus">The InMemoryAwsBus instance to associate with the SNS client.</param>
    /// <returns>An InMemorySnsClient instance connected to the provided InMemoryAwsBus.</returns>
    public static InMemorySnsClient CreateSnsClient(this InMemoryAwsBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return new InMemorySnsClient(bus);
    }
}