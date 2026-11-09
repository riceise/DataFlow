namespace DataFlow.IntegrationTests.Fixtures;

// XLSX counterpart to MixedCustomerCsv: identical logical 200-row seed and
// 1000-row mixed dataset, using Excel native shared strings, inline strings,
// numeric serial dates with style, and Excel booleans.
internal static class MixedCustomerXlsx
{
    private static readonly string[] Headers = ["externalId", "name", "email", "registeredAt", "isActive"];

    public static byte[] Seed()
    {
        var rows = new List<IReadOnlyList<XlsxCellSpec>>(200);
        foreach (var group in Enumerable.Range(0, 100))
        {
            rows.Add(Row(group, 1, "Before update"));
            rows.Add(Row(group, 2, MixedCustomerCsv.Name(group, 2)));
        }

        return XlsxTestBuilder.BuildSingleSheet(Headers, rows);
    }

    public static byte[] File()
    {
        var rows = new List<IReadOnlyList<XlsxCellSpec>>(MixedCustomerCsv.Total);
        foreach (var group in Enumerable.Range(0, 100))
        {
            rows.Add(Row(group, 0, "  " + MixedCustomerCsv.Name(group, 0) + "  ", trimKey: true, useInlineString: true));
            rows.Add(Row(group, 1, MixedCustomerCsv.Name(group, 1)));
            rows.Add(Row(group, 2, MixedCustomerCsv.Name(group, 2)));
            rows.Add(Row(group, 3, ""));
            rows.Add(Row(group, 4, MixedCustomerCsv.Name(group, 4), active: "yes"));
            rows.Add(Row(group, 5, new string('x', 41)));
            rows.Add(Row(group, 6, MixedCustomerCsv.Name(group, 6), email: "invalid-email"));
            rows.Add(Row(group, 7, MixedCustomerCsv.Name(group, 7), date: "2026-02-30"));
            rows.Add(Row(group, 0, MixedCustomerCsv.Name(group, 0)));
            rows.Add(Row(group, 0, "Different values", email: "changed@example.test"));
        }

        return XlsxTestBuilder.BuildSingleSheet(Headers, rows);
    }

    private static IReadOnlyList<XlsxCellSpec> Row(
        int group,
        int slot,
        string name,
        string? email = null,
        string date = "2026-01-15",
        string active = "true",
        bool trimKey = false,
        bool useInlineString = false)
    {
        var keyText = $"{(trimKey ? "  " : "")}{MixedCustomerCsv.Key(group, slot)}{(trimKey ? "  " : "")}";
        var keyCell = useInlineString ? XlsxCellSpec.InlineString(keyText) : XlsxCellSpec.SharedString(keyText);
        var nameCell = useInlineString ? XlsxCellSpec.InlineString(name) : XlsxCellSpec.SharedString(name);
        var emailCell = XlsxCellSpec.SharedString(email ?? MixedCustomerCsv.Email(group, slot));
        var dateCell = date == "2026-01-15"
            ? XlsxCellSpec.DateSerial(new DateOnly(2026, 1, 15))
            : XlsxCellSpec.SharedString(date);
        var activeCell = active switch
        {
            "true" => XlsxCellSpec.Boolean(true),
            "false" => XlsxCellSpec.Boolean(false),
            _ => XlsxCellSpec.SharedString(active)
        };

        return [keyCell, nameCell, emailCell, dateCell, activeCell];
    }
}
