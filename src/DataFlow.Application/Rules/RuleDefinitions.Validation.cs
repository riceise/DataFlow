using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataFlow.Application.Rules;

public static partial class RuleDefinitions
{
    private static RuleDefinition ValidateDefinition(JsonElement element)
    {
        var issues = new List<RuleDefinitionIssue>();
        void Add(string code, string path, string message, string hint, string? field = null) =>
            issues.Add(new(code, path, message, hint, field));

        if (element.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(element.GetRawText()) > 256 * 1024)
        {
            Add("definition_size", "$", "Определение отсутствует или превышает 256 КиБ.", "Укажите JSON-объект размером до 256 КиБ.");
            throw new RuleDefinitionValidationException(issues);
        }

        // Inspect the original JSON before deserialization: retain paths, duplicates and independent type errors.
        CheckObject(element, "", new RuleDefinition());
        if (issues.Count > 0) throw new RuleDefinitionValidationException(issues);
        RuleDefinition definition;
        try { definition = element.Deserialize<RuleDefinition>(Json)!; }
        catch (JsonException ex)
        {
            Add("invalid_json_value", ex.Path?.TrimStart('$', '.') is { Length: > 0 } path ? path : "$",
                "Значение JSON не удалось прочитать.", "Проверьте тип значения и глубину вложенности: не более 16 уровней.");
            throw new RuleDefinitionValidationException(issues);
        }

        if (definition.DefinitionFormatVersion != 1)
            Add("unsupported_version", "definitionFormatVersion", "Неподдерживаемая версия формата.", "Укажите 1.");
        if (definition.DataType is not ("Customer" or "Tabular"))
            Add("unsupported_data_type", "dataType", "Неизвестный тип данных.", "Используйте Tabular или Customer.");
        if (definition.Fields.Length is 0 or > 64)
            Add("field_count", "fields", "Нужно от 1 до 64 полей.", "Добавьте поля или уменьшите их количество до 64.");
        if (definition.Csv.Delimiter is not { Length: 1 } || definition.Csv.Delimiter[0] is '"' or '\r' or '\n' or '\0')
            Add("invalid_delimiter", "csv.delimiter", "Недопустимый разделитель CSV.", "Укажите один символ, например «;» или «,», кроме кавычки, переноса строки и нулевого символа.");
        if (definition.Columns.Unknown is not ("reject" or "ignore"))
            Add("unknown_columns_mode", "columns.unknown", "Неизвестный режим обработки лишних столбцов.", "Укажите reject или ignore.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < definition.Fields.Length; i++)
        {
            var f = definition.Fields[i];
            var path = $"fields[{i}]";
            void Field(string code, string property, string message, string hint) => Add(code, $"{path}.{property}", message, hint, f.Name);
            if (!Regex.IsMatch(f.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant))
                Field("invalid_field_name", "name", "Недопустимое имя поля.", "Используйте 1–64 латинских буквы, цифры или _, начиная с буквы или _.");
            if (!names.Add(f.Name))
                Field("duplicate_field_name", "name", $"Имя «{f.Name}» уже используется.", "Задайте уникальное имя поля.");
            var header = (f.Header ?? f.Name).Trim();
            if (header.Length is 0 or > 200)
                Field("invalid_header", "header", "Заголовок пустой или длиннее 200 символов.", "Укажите заголовок длиной 1–200 символов.");
            if (!headers.Add(header))
                Field("duplicate_header", f.Header is null ? "name" : "header", $"Заголовок «{header}» уже используется.", "Задайте уникальный заголовок; регистр и пробелы по краям не учитываются.");
            var supported = f.Type is "string" or "email" or "integer" or "decimal" or "date" or "boolean";
            if (!supported)
                Field("unsupported_field_type", "type", $"Тип «{f.Type}» не поддерживается.", "Используйте string, email, integer, decimal, date или boolean.");

            var text = f.Type is "string" or "email";
            var numeric = f.Type is "integer" or "decimal";
            if (supported)
            {
                foreach (var (property, present, replacement) in new[] {
                    ("minimum", f.Minimum is not null, "minLength"), ("maximum", f.Maximum is not null, "maxLength") })
                    if (present && !numeric)
                        Field("constraint_type_mismatch", property,
                            $"Свойство {property} задаёт числовую границу и применяется только к integer и decimal; указан тип {f.Type}.",
                            text ? $"Если нужно ограничить длину текста, замените {property} на {replacement} (целое число символов)." : $"Удалите {property} или выберите числовой тип поля.");
                foreach (var (property, present, replacement) in new[] {
                    ("minLength", f.MinLength is not null, "minimum"), ("maxLength", f.MaxLength is not null, "maximum"),
                    ("regexPattern", f.RegexPattern is not null, "") })
                    if (present && !text)
                        Field("constraint_type_mismatch", property, $"Свойство {property} применяется только к string и email; указан тип {f.Type}.",
                            numeric && replacement.Length > 0 ? $"Если нужно ограничить числовое значение, используйте {replacement}." : $"Удалите {property} или выберите текстовый тип поля.");
            }
            if (f.MinLength is < 0 or > 65536)
                Field("invalid_min_length", "minLength", "Недопустимая минимальная длина.", "Укажите целое число от 0 до 65536.");
            if (f.MaxLength is <= 0 or > 65536)
                Field("invalid_max_length", "maxLength", "Недопустимая максимальная длина.", "Укажите целое число от 1 до 65536.");
            if (f.MinLength > f.MaxLength)
                Field("length_range", "minLength", "minLength больше maxLength.", "Уменьшите minLength или увеличьте maxLength.");
            if (f.Minimum > f.Maximum)
                Field("numeric_range", "minimum", "minimum больше maximum.", "Уменьшите minimum или увеличьте maximum.");
            if (f.Format is not null && (f.Type != "date" || f.Format != "yyyy-MM-dd"))
                Field("invalid_format", "format", "Формат допустим только для date и должен быть yyyy-MM-dd.", "Для даты укажите yyyy-MM-dd; для остальных типов удалите format.");
            if (f.AllowedValues is { } values)
            {
                if (values.Length is 0 or > 200)
                    Field("allowed_values_count", "allowedValues", "Нужно от 1 до 200 допустимых значений.", "Исправьте список или удалите allowedValues.");
                for (var j = 0; j < values.Length; j++)
                {
                    var value = values[j];
                    if (value.Length > 256 || value.Trim().Length == 0)
                        Field("invalid_allowed_value", $"allowedValues[{j}]", "Допустимое значение пустое или длиннее 256 символов.", "Укажите непустую строку длиной до 256 символов.");
                    else if (supported && !TryValue(f.Type, value.Trim(), out _))
                        Field("allowed_value_type", $"allowedValues[{j}]", $"Значение не соответствует типу {f.Type}.", "Для чисел используйте точку, для дат — yyyy-MM-dd, для boolean — true или false; для email — полный адрес.");
                }
            }
            if (f.RegexPattern is { } pattern)
            {
                if (pattern.Length > 256)
                    Field("regex_length", "regexPattern", "Регулярное выражение длиннее 256 символов.", "Сократите выражение до 256 символов.");
                else
                    try { _ = CompileRegex(pattern); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        Field("invalid_regex", "regexPattern", "Регулярное выражение некорректно или использует неподдерживаемые конструкции.", "Проверьте синтаксис; обратные ссылки и проверки вперёд/назад не поддерживаются.");
                    }
            }
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < definition.KeyFields.Length; i++)
        {
            var key = definition.KeyFields[i];
            if (!keys.Add(key)) Add("duplicate_key", $"keyFields[{i}]", $"Ключ «{key}» повторяется.", "Удалите повторяющийся ключ.");
            if (!names.Contains(key)) Add("unknown_key_field", $"keyFields[{i}]", $"Поля «{key}» нет в fields.", "Укажите name существующего поля или добавьте это поле в fields.");
        }
        if (definition.DataType == "Customer")
        {
            if (!definition.KeyFields.SequenceEqual(["externalId"]))
                Add("customer_key", "keyFields", "Для Customer ключом должен быть externalId.", "Укажите keyFields: [\"externalId\"].");
            foreach (var name in new[] { "externalId", "name", "email", "isActive" })
                if (!definition.Fields.Any(f => f.Name == name && f.Required))
                    Add("customer_required_field", "fields", $"Для Customer обязательно поле «{name}» с required: true.", "Добавьте поле или сделайте его обязательным.", name);
            for (var i = 0; i < definition.Fields.Length; i++)
            {
                var f = definition.Fields[i];
                var type = f.Name switch { "externalId" or "name" => "string", "email" => "email", "registeredAt" => "date", "isActive" => "boolean", _ => "" };
                var max = f.Name switch { "externalId" => 80, "name" => 200, "email" => 254, _ => 0 };
                if (f.Type != type)
                    Add("customer_field_type", $"fields[{i}].type", $"Поле «{f.Name}» не соответствует формату Customer.", type.Length == 0 ? "Удалите это поле или используйте dataType: Tabular." : $"Укажите тип {type}.", f.Name);
                if (max > 0 && (f.MaxLength is null || f.MaxLength > max))
                    Add("customer_max_length", $"fields[{i}].maxLength", $"Для «{f.Name}» требуется maxLength не больше {max}.", $"Укажите maxLength от 1 до {max}.", f.Name);
            }
        }
        if (issues.Count > 0) throw new RuleDefinitionValidationException(issues);
        return definition;

        void CheckObject(JsonElement value, string path, object model)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Add("expected_object", path.Length == 0 ? "$" : path, "Ожидается JSON-объект.", "Укажите объект в фигурных скобках { ... }.");
                return;
            }
            var properties = model.GetType().GetProperties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                var location = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                if (!seen.Add(property.Name)) Add("duplicate_property", location, $"Свойство «{property.Name}» указано несколько раз.", "Оставьте одно значение свойства.");
                if (!properties.TryGetValue(property.Name, out var info))
                {
                    Add("unknown_property", location, $"Неизвестное свойство «{property.Name}».", $"Допустимые свойства: {string.Join(", ", properties.Values.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)))}.");
                    continue;
                }
                var type = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
                var item = property.Value;
                if (item.ValueKind == JsonValueKind.Null && (Nullable.GetUnderlyingType(info.PropertyType) is not null || info.Name is "Header" or "RegexPattern" or "Format" or "AllowedValues")) continue;
                if (type == typeof(CsvDefinition)) { CheckObject(item, location, new CsvDefinition()); continue; }
                if (type == typeof(ColumnDefinition)) { CheckObject(item, location, new ColumnDefinition()); continue; }
                if (type == typeof(RuleField[]) || type == typeof(string[]))
                {
                    if (item.ValueKind != JsonValueKind.Array)
                        Add("expected_array", location, "Ожидается массив.", "Укажите список в квадратных скобках [ ... ].");
                    else
                    {
                        var index = 0;
                        foreach (var child in item.EnumerateArray())
                        {
                            if (type == typeof(RuleField[])) CheckObject(child, $"{location}[{index}]", new RuleField());
                            else if (child.ValueKind != JsonValueKind.String)
                                Add("expected_string", $"{location}[{index}]", "Элемент должен быть строкой.", "Заключите значение в двойные кавычки.");
                            index++;
                        }
                    }
                    continue;
                }
                var valid = type == typeof(string) ? item.ValueKind == JsonValueKind.String :
                    type == typeof(bool) ? item.ValueKind is JsonValueKind.True or JsonValueKind.False :
                    type == typeof(int) ? item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _) :
                    type == typeof(decimal) && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out _);
                if (!valid)
                    Add("invalid_property_type", location, $"Неверный тип значения свойства «{property.Name}».",
                        type == typeof(string) ? "Укажите строку в двойных кавычках." : type == typeof(bool) ? "Укажите true или false без кавычек." : type == typeof(int) ? "Укажите целое число без кавычек в диапазоне −2147483648…2147483647." : "Укажите число без кавычек, используя точку как десятичный разделитель (в диапазоне decimal).");
            }
        }
    }
}
