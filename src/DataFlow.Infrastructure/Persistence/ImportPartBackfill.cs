namespace DataFlow.Infrastructure.Persistence;

/// <summary>
/// Deterministic, repeatable data migration kept separate from schema expansion and enforcement.
/// The statement batch bounds per-statement row work; the controlled cutover still runs in one
/// migration transaction, so production rollout must measure its lock and WAL budget.
/// </summary>
public static class ImportPartBackfill
{
    public const string Sql = """
        INSERT INTO import_parts (
            id, import_job_id, part_index, part_type, source_name, sheet_name, sheet_index, source_locator,
            status, last_committed_physical_row_number, total_rows, processed_rows, valid_rows, inserted_rows,
            updated_rows, invalid_rows, duplicate_rows, started_at, completed_at, last_error_code,
            last_error_row_number, rule_set_version_id, rule_document_id, rule_snapshot_hash,
            rule_set_name_at_submission, rule_version_number_at_submission, deduplication_scope, created_at, updated_at)
        SELECT
            gen_random_uuid(), j.id, 0,
            CASE WHEN right(lower(j.original_file_name), 5) = '.xlsx' THEN 2
                 WHEN right(lower(j.original_file_name), 4) = '.csv' THEN 1 ELSE 4 END,
            j.original_file_name, NULL, NULL,
            CASE WHEN right(lower(j.original_file_name), 5) = '.xlsx' THEN 'xlsx:first-sheet' ELSE 'file:single-file' END,
            CASE
                WHEN j.status = 1 AND j.attempt_count = 0 THEN 1
                WHEN j.status IN (1, 2, 3) THEN 2
                WHEN j.status = 4 THEN 3
                WHEN j.status = 5 THEN 4
                WHEN j.status = 6 AND j.retryable = 0 THEN 5
                WHEN j.status = 6 THEN 2
                WHEN j.status = 7 THEN 6
            END,
            j.last_committed_row_number, j.total_rows, j.processed_rows, j.valid_rows, j.inserted_rows,
            j.updated_rows, j.invalid_rows, j.duplicate_rows, j.started_at, j.completed_at, j.last_error_code,
            NULL,
            CASE WHEN j.rule_set_version_id IS NOT NULL AND j.rule_document_id IS NOT NULL THEN j.rule_set_version_id END,
            CASE WHEN j.rule_set_version_id IS NOT NULL AND j.rule_document_id IS NOT NULL THEN j.rule_document_id END,
            CASE WHEN j.rule_set_version_id IS NOT NULL AND j.rule_document_id IS NOT NULL THEN d.content_hash END,
            CASE WHEN j.rule_set_version_id IS NOT NULL AND j.rule_document_id IS NOT NULL THEN j.rule_set_name_at_submission END,
            CASE WHEN j.rule_set_version_id IS NOT NULL AND j.rule_document_id IS NOT NULL THEN j.rule_version_number_at_submission END,
            CASE WHEN s.data_type = 2 THEN 'part:0' ELSE 'customer:job' END,
            j.created_at, j.updated_at
        FROM import_jobs j
        JOIN data_sources s ON s.id = j.data_source_id
        LEFT JOIN rule_documents d ON d.id = j.rule_document_id
        WHERE NOT EXISTS (SELECT 1 FROM import_parts existing WHERE existing.import_job_id = j.id);

        DO $backfill$
        DECLARE
            affected integer;
        BEGIN
            LOOP
                WITH batch AS (
                    SELECT r.id, r.import_job_id
                    FROM import_rows r
                    WHERE r.import_part_id IS NULL
                    ORDER BY r.id
                    FOR UPDATE
                    LIMIT 5000
                ), updated AS (
                    UPDATE import_rows r
                    SET import_part_id = p.id
                    FROM batch b
                    JOIN import_parts p ON p.import_job_id = b.import_job_id AND p.part_index = 0
                    WHERE r.id = b.id
                    RETURNING r.id
                )
                SELECT count(*) INTO affected FROM updated;
                EXIT WHEN affected = 0;
            END LOOP;
        END
        $backfill$;
        """;
}
