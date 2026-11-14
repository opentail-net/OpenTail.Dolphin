namespace OpenTail.Dolphin.Files.Contracts;

/// <summary>
/// The source refused the credential, or the scope or access it was given (a 401 or 403, or the provider's equivalent). A connector throws it
/// only when it can tell that from the answer: a throttling answer (429, or a provider's quota error) is never this. The pipeline stops the run
/// on it (ingestion-ideas section 6, A2): the item is not put on the retry list, and the cursor is not saved past the last commit, so the next
/// run starts from the same place once the credential is fixed.
/// </summary>
public sealed class SourceConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
