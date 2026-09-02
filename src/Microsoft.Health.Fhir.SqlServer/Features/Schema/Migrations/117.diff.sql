IF object_id('dbo.EmbeddingModel') IS NULL
CREATE TABLE dbo.EmbeddingModel
(
    EmbeddingModelId    smallint IDENTITY(1,1) NOT NULL,
    ModelName           varchar(128) NOT NULL,
    ModelVersion        varchar(64)  NOT NULL,
    Dimension           int          NOT NULL,
    DistanceMetric      varchar(16)  NOT NULL CONSTRAINT DF_EmbeddingModel_DistanceMetric DEFAULT 'cosine',
    CreatedAt           datetime2(7) NOT NULL CONSTRAINT DF_EmbeddingModel_CreatedAt DEFAULT sysutcdatetime(),

    CONSTRAINT PKC_EmbeddingModel PRIMARY KEY CLUSTERED (EmbeddingModelId),
    CONSTRAINT U_EmbeddingModel_ModelName_ModelVersion UNIQUE (ModelName, ModelVersion)
)
GO

IF object_id('dbo.VectorSearchParam') IS NULL
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
GO

ALTER TABLE dbo.VectorSearchParam SET ( LOCK_ESCALATION = AUTO )
GO

IF EXISTS (SELECT 1 FROM sys.types WHERE name = N'vector' AND is_user_defined = 0)
    AND COL_LENGTH('dbo.VectorSearchParam', 'Embedding') IS NULL
    EXEC(N'ALTER TABLE dbo.VectorSearchParam ADD Embedding vector(1536) NULL')
GO

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = N'VectorSearchParamList')
CREATE TYPE dbo.VectorSearchParamList AS TABLE
(
    ResourceTypeId           smallint      NOT NULL
   ,ResourceSurrogateId      bigint        NOT NULL
   ,SearchParamId            smallint      NOT NULL
   ,ChunkOrdinal             smallint      NOT NULL
   ,Embedding                varchar(max)  NOT NULL
   ,EmbeddingModelId         smallint      NOT NULL
   ,SourceTextHash           varbinary(32) NULL
)
GO
