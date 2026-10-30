using System.Text;

namespace DataFlow.IntegrationTests.Fixtures;

// Deterministic synthetic fixture. Expectations are defined by slot, independently
// of the production parser, normalizer and hashing code. 100 groups × 10 data rows.
internal static class MixedCustomerCsv
{
    public const string Header = "externalId,name,email,registeredAt,isActive\n";
    public const int Total = 1000;
    public const int Inserted = 100;
    public const int Updated = 100;
    public const int Duplicate = 100;
    public const int Invalid = 700;

    public static string Key(int group, int slot) => $"customer-{group:D3}-{slot}";
    public static string Email(int group, int slot) => $"customer-{group:D3}-{slot}@example.test";
    public static string Name(int group, int slot) => slot == 0 ? $"Клиент, {group:D3}" : $"Customer {group:D3}-{slot}";

    public static byte[] Seed() => Encoding.UTF8.GetBytes(Header + string.Concat(Enumerable.Range(0, 100).SelectMany(group =>
        new[] { Line(group, 1, "Before update"), Line(group, 2, Name(group, 2)) })));

    public static byte[] File()
    {
        var csv = new StringBuilder(Header);
        foreach (var group in Enumerable.Range(0, 100))
        {
            csv.Append(Line(group, 0, "  " + Name(group, 0) + "  ", trimKey: true));
            csv.Append(Line(group, 1, Name(group, 1)));
            csv.Append(Line(group, 2, Name(group, 2)));
            csv.Append(Line(group, 3, ""));
            csv.Append(Line(group, 4, Name(group, 4), active: "yes"));
            csv.Append(Line(group, 5, new string('x', 41)));
            csv.Append(Line(group, 6, Name(group, 6), email: "invalid-email"));
            csv.Append(Line(group, 7, Name(group, 7), date: "2026-02-30"));
            csv.Append(Line(group, 0, Name(group, 0)));
            csv.Append(Line(group, 0, "Different values", email: "changed@example.test"));
        }
        // Exercise UTF-8/BOM, quoting, Unicode and schema-driven trim through S3.
        return [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static (string Status, string? Field, string? Code) Expected(int index) => (index % 10) switch
    {
        0 => ("Inserted", null, null),
        1 => ("Updated", null, null),
        2 => ("Duplicate", null, null),
        3 => ("Invalid", "name", "required"),
        4 => ("Invalid", "isActive", "invalid_type"),
        5 => ("Invalid", "name", "max_length"),
        6 => ("Invalid", "email", "invalid_email"),
        7 => ("Invalid", "registeredAt", "invalid_date"),
        _ => ("Invalid", "$row", "duplicate_key_in_file")
    };

    private static string Line(int group, int slot, string name, string? email = null,
        string date = "2026-01-15", string active = "true", bool trimKey = false) =>
        $"{(trimKey ? "  " : "")}{Key(group, slot)}{(trimKey ? "  " : "")},\"{name}\",{email ?? Email(group, slot)},{date},{active}\n";
}
