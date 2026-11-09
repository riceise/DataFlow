namespace DataFlow.Domain.Entities;

// Documents and versions are append-only; mutations are confined to set metadata and access records.
public sealed class RuleDocument
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EngineVersion { get; private set; } = "rules-v1";
    public int DefinitionFormatVersion { get; private set; } = 1;
    public string ContentHash { get; private set; } = "";
    public string DefinitionJson { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private RuleDocument() { }
    public RuleDocument(string hash, string json, string engine = "rules-v1")
    { ContentHash = hash; DefinitionJson = json; EngineVersion = engine; }
}

public sealed class RuleSet
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public string DataType { get; private set; } = "Tabular";
    public int LastVersionNumber { get; private set; }
    public int Revision { get; private set; } = 1;
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private RuleSet() { }
    public RuleSet(Guid owner, string name, string? description, string type)
    { OwnerUserId = owner; Name = name; Description = description; DataType = type; }
    public int NextVersion() { LastVersionNumber++; Touch(); return LastVersionNumber; }
    public void RegisterHistoricalVersion(int number)
    {
        if (number <= LastVersionNumber)
        {
            throw new InvalidOperationException("Historical versions must be registered in increasing order.");
        }

        LastVersionNumber = number;
        Touch();
    }
    public void Update(string name, string? description, bool archived)
    { Name = name; Description = description; IsArchived = archived; Touch(); }
    private void Touch() { Revision++; UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class RuleSetVersion
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RuleSetId { get; private set; }
    public int VersionNumber { get; private set; }
    public Guid RuleDocumentId { get; private set; }
    public Guid? BasedOnVersionId { get; private set; }
    public string? Label { get; private set; }
    public string? ChangeNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private RuleSetVersion() { }
    public RuleSetVersion(Guid setId, int number, Guid documentId, string? label, string? note, Guid? basedOn)
    { RuleSetId = setId; VersionNumber = number; RuleDocumentId = documentId; Label = label; ChangeNote = note; BasedOnVersionId = basedOn; }
}

public sealed class RuleVersionGrant
{
    public Guid RuleSetVersionId { get; set; }
    public Guid RecipientUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RuleVersionPublication
{
    public Guid RuleSetVersionId { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserRuleLibraryEntry
{
    public Guid UserId { get; set; }
    public Guid RuleSetVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
