using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataFlow.Infrastructure.Persistence;

internal sealed class RuleDocumentConfiguration : IEntityTypeConfiguration<RuleDocument>
{
    public void Configure(EntityTypeBuilder<RuleDocument> b)
    {
        b.ToTable("rule_documents"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DefinitionJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.EngineVersion).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.DefinitionFormatVersion, x.EngineVersion, x.ContentHash }).IsUnique();
    }
}
internal sealed class RuleSetConfiguration : IEntityTypeConfiguration<RuleSet>
{
    public void Configure(EntityTypeBuilder<RuleSet> b)
    {
        b.ToTable("rule_sets", t => t.HasCheckConstraint("ck_rule_sets_type", "data_type IN ('Customer', 'Tabular')")); b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.OwnerUserId, x.IsArchived, x.UpdatedAt });
    }
}
internal sealed class RuleSetVersionConfiguration : IEntityTypeConfiguration<RuleSetVersion>
{
    public void Configure(EntityTypeBuilder<RuleSetVersion> b)
    {
        b.ToTable("rule_set_versions", t => t.HasCheckConstraint("ck_rule_versions_number", "version_number > 0")); b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Label).HasMaxLength(100); b.Property(x => x.ChangeNote).HasMaxLength(2000);
        b.HasIndex(x => new { x.RuleSetId, x.VersionNumber }).IsUnique();
        b.HasAlternateKey(x => new { x.Id, x.RuleDocumentId });
        b.HasOne<RuleSet>().WithMany().HasForeignKey(x => x.RuleSetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RuleDocument>().WithMany().HasForeignKey(x => x.RuleDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RuleSetVersion>().WithMany().HasForeignKey(x => x.BasedOnVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class GrantConfiguration : IEntityTypeConfiguration<RuleVersionGrant>
{
    public void Configure(EntityTypeBuilder<RuleVersionGrant> b)
    {
        b.ToTable("rule_version_grants"); b.HasKey(x => new { x.RuleSetVersionId, x.RecipientUserId });
        b.HasIndex(x => new { x.RecipientUserId, x.CreatedAt });
        b.HasOne<RuleSetVersion>().WithMany().HasForeignKey(x => x.RuleSetVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class PublicationConfiguration : IEntityTypeConfiguration<RuleVersionPublication>
{
    public void Configure(EntityTypeBuilder<RuleVersionPublication> b)
    {
        b.ToTable("rule_version_publications"); b.HasKey(x => x.RuleSetVersionId);
        b.Property(x => x.RuleSetVersionId).ValueGeneratedNever();
        b.HasIndex(x => x.PublishedAt);
        b.HasOne<RuleSetVersion>().WithMany().HasForeignKey(x => x.RuleSetVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class LibraryConfiguration : IEntityTypeConfiguration<UserRuleLibraryEntry>
{
    public void Configure(EntityTypeBuilder<UserRuleLibraryEntry> b)
    {
        b.ToTable("user_rule_library_entries"); b.HasKey(x => new { x.UserId, x.RuleSetVersionId });
        b.HasOne<RuleSetVersion>().WithMany().HasForeignKey(x => x.RuleSetVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
