CREATE TABLE dbo.VectorSearchParam
(
    ResourceTypeId      smallint   NOT NULL,
    ResourceSurrogateId bigint     NOT NULL,
    SearchParamId       smallint   NOT NULL,
    ChunkOrdinal        smallint   NOT NULL CONSTRAINT DF_VectorSearchParam_ChunkOrdinal DEFAULT 0,
    EmbeddingModelId    smallint   NOT NULL,
    SourceTextHash      varbinary(32) NULL,

    CONSTRAINT PKC_VectorSearchParam PRIMARY KEY CLUSTERED (ResourceTypeId, ResourceSurrogateId, SearchParamId, ChunkOrdinal)
    WITH (DATA_COMPRESSION = PAGE)
)

ALTER TABLE dbo.VectorSearchParam SET ( LOCK_ESCALATION = AUTO )

-- The Embedding column is added via dynamic SQL so that (a) the schema code generator,
-- which parses CREATE TABLE statements, never sees the `vector` type (it would emit a
-- non-existent NullableVectorColumn), and (b) the script still applies on engines that
-- lack VECTOR support. See docs: Azure SQL vector data type.
IF EXISTS (SELECT 1 FROM sys.types WHERE name = N'vector' AND is_user_defined = 0)
    EXEC(N'ALTER TABLE dbo.VectorSearchParam ADD Embedding vector(1536) NULL')
