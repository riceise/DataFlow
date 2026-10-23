namespace DataFlow.Domain.Enums;

public enum DataSourceDataType
{
    Customer = 1
}

[Flags]
public enum AcceptedFileFormat
{
    None = 0,
    Csv = 1,
    Xlsx = 2,
    Pdf = 4,
    Zip = 8,
    All = Csv | Xlsx
}

public enum ImportJobStatus
{
    Queued = 1,
    Processing = 2,
    RetryScheduled = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    Failed = 6,
    Cancelled = 7
}

public enum ImportRowStatus
{
    Inserted = 1,
    Updated = 2,
    Invalid = 3,
    Duplicate = 4
}
