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
