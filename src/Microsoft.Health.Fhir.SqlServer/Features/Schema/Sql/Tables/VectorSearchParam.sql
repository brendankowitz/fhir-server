CREATE TABLE dbo.VectorSearchParam
(
    ResourceTypeId      smallint   NOT NULL,
    ResourceSurrogateId bigint     NOT NULL,
    SearchParamId       smallint   NOT NULL,
    ChunkOrdinal        smallint   NOT NULL CONSTRAINT DF_VectorSearchParam_ChunkOrdinal DEFAULT 0,
    EmbeddingModelId    smallint   NOT NULL,
    SourceTextHash        varbinary(32)  NULL,
    SourceTextCompressed  varbinary(max) NULL,

    CONSTRAINT PKC_VectorSearchParam PRIMARY KEY CLUSTERED (ResourceTypeId, ResourceSurrogateId, SearchParamId, ChunkOrdinal)
    WITH (DATA_COMPRESSION = PAGE)
)

ALTER TABLE dbo.VectorSearchParam SET ( LOCK_ESCALATION = AUTO )

-- The Embedding column is added via dynamic SQL so the schema code generator, which parses
-- CREATE TABLE statements, never sees the `vector` type (it would emit a non-existent
-- NullableVectorColumn -> CS0246). VECTOR support is a hard requirement for schema V117:
-- per ADR 2605 the migration fails fast with a clear operator error rather than degrading.
IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = N'vector' AND is_user_defined = 0)
    THROW 50000, 'FHIR schema V117 requires a SQL engine that supports the VECTOR type (Azure SQL Database). Upgrade the database engine, or remain on schema V116.', 1;

EXEC(N'ALTER TABLE dbo.VectorSearchParam ADD Embedding vector(1536) NULL')
