#if NET
using System.Diagnostics;
using System.Net;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// The activity a wire protocol records for each operation it serves (<c>SQS.SendMessage</c>,
/// <c>DynamoDB.PutItem</c>, ...), whichever host the request came through.
/// </summary>
internal static class WireTracing
{
    /// <summary>
    /// Starts the activity for <paramref name="operation"/> of <paramref name="service"/>; null, without
    /// building its name, when nothing listens.
    /// </summary>
    public static Activity? Start(string service, string operation) =>
        InMemoryAwsBus.ActivitySource.HasListeners()
            ? InMemoryAwsBus.ActivitySource.StartActivity($"{service}.{operation}", ActivityKind.Client)
            : null;

    /// <summary>Marks <paramref name="activity"/> failed when the operation was answered with an error.</summary>
    public static void Complete(Activity? activity, HttpStatusCode status, Exception? exception)
    {
        if (activity is null || (int)status < 400)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception?.Message);
        if (exception is not null)
        {
            activity.SetTag("error.type", exception.GetType().FullName);
        }
    }
}
#endif
