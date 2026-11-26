using System.Runtime.CompilerServices;

namespace OpenTail.Dolphin.Files.Inbox;

/// <summary>
/// Batch 14 (ingestion ideas, A2 on the inbox adapters). An inbox adapter's read is an enumeration, and a provider's refusal arrives as an
/// exception from inside it. A try block cannot wrap a yield, so this moves each step of the enumeration into a guarded call and yields the
/// element outside it. The adapter supplies the classification: it returns the <see cref="SourceConfigurationException"/> for a refused
/// sign-in, or null for anything else, which is then rethrown as it was.
/// </summary>
public static class ProviderRefusal
{
    public static async IAsyncEnumerable<T> ClassifyAsync<T>(IAsyncEnumerable<T> source, Func<Exception, Exception?> classify,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool more;
            try
            {
                more = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                var refusal = classify(ex);
                if (refusal is null) throw;
                throw refusal;
            }

            if (!more) yield break;
            yield return enumerator.Current;
        }
    }
}
