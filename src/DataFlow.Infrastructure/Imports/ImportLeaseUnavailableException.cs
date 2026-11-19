namespace DataFlow.Infrastructure.Imports;

// The consumer acknowledges this delivery; reconciliation recovers the job while another owner may still be processing.
public sealed class ImportLeaseUnavailableException() : Exception("Import lease is unavailable or has been lost.");
