namespace DataFlow.Infrastructure.Imports;

// The delivery must remain unacknowledged: another owner may still be processing.
public sealed class ImportLeaseUnavailableException() : Exception("Import lease is unavailable or has been lost.");
