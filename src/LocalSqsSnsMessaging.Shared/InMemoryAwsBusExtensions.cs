using System.ComponentModel;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Provides extension methods for the InMemoryAwsBus class to create SQS and SNS clients.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class InMemoryAwsBusExtensions
{
    /// <param name="bus">The InMemoryAwsBus instance to associate with the SQS client.</param>
    extension(InMemoryAwsBus bus)
    {
        /// <summary>
        /// Creates an in-memory SQS client associated with the specified InMemoryAwsBus instance.
        /// </summary>
        /// <returns>An InMemorySqsClient instance connected to the provided InMemoryAwsBus.</returns>
        public InMemorySqsClient CreateRawSqsClient()
        {
            return new InMemorySqsClient(bus);
        }

        /// <summary>
        /// Creates an in-memory SNS client associated with the specified InMemoryAwsBus instance.
        /// </summary>
        /// <returns>An InMemorySnsClient instance connected to the provided InMemoryAwsBus.</returns>
        public InMemorySnsClient CreateRawSnsClient()
        {
            return new InMemorySnsClient(bus);
        }
    }
}
