namespace DataFlow.Application.Imports;

public static class ImportRowKeyPolicy
{
    public static bool BlocksKeyReservation(string errorCode) =>
        errorCode is "required"
            or "invalid_type"
            or "invalid_email"
            or "invalid_date"
            or "formula_cell_not_supported"
            or "unsupported_cell_value";
}
