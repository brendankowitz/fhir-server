CREATE PROCEDURE dbo.MergeVectorSearchParams
     @ResourceTypeId smallint
    ,@ResourceSurrogateIds dbo.BigintList READONLY
    ,@VectorSearchParams dbo.VectorSearchParamList READONLY
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'RT='+convert(varchar,@ResourceTypeId)
                            +' R='+convert(varchar,(SELECT count(*) FROM @ResourceSurrogateIds))
                            +' VSP='+convert(varchar,(SELECT count(*) FROM @VectorSearchParams))
       ,@st datetime = getUTCdate()
       ,@Rows int = 0

BEGIN TRY
  SET TRANSACTION ISOLATION LEVEL SERIALIZABLE

  BEGIN TRANSACTION

  DELETE V
    FROM dbo.VectorSearchParam V
    WHERE V.ResourceTypeId = @ResourceTypeId
      AND EXISTS (
      SELECT *
        FROM @ResourceSurrogateIds R
        WHERE R.Id = V.ResourceSurrogateId)
  SET @Rows += @@rowcount

  INSERT INTO dbo.VectorSearchParam
         ( ResourceTypeId, ResourceSurrogateId, SearchParamId, ChunkOrdinal, EmbeddingModelId, SourceTextHash, SourceTextCompressed, Embedding )
    SELECT ResourceTypeId, ResourceSurrogateId, SearchParamId, ChunkOrdinal, EmbeddingModelId, SourceTextHash, SourceTextCompressed, CAST(Embedding AS VECTOR(1536))
      FROM @VectorSearchParams
  SET @Rows += @@rowcount

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Action='Merge',@Rows=@Rows
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION;
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error',@Start=@st;
  THROW
END CATCH
GO
