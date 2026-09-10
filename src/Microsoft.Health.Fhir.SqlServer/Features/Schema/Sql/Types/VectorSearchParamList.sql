--DROP TYPE dbo.VectorSearchParamList
GO
CREATE TYPE dbo.VectorSearchParamList AS TABLE
(
    ResourceTypeId           smallint      NOT NULL
   ,ResourceSurrogateId      bigint        NOT NULL
   ,SearchParamId            smallint      NOT NULL
   ,ChunkOrdinal             smallint      NOT NULL
   ,Embedding                varchar(max)  NOT NULL
   ,EmbeddingModelId         smallint      NOT NULL
   ,SourceTextHash           varbinary(32) NULL
   ,SourceTextCompressed     varbinary(max) NULL
)
GO
