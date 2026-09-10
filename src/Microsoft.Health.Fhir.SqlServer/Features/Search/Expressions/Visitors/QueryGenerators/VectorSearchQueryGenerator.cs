// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text.Json;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.SqlServer;
using Microsoft.Health.SqlServer.Features.Schema.Model;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors.QueryGenerators
{
    internal sealed class VectorSearchQueryGenerator : SearchParamTableExpressionQueryGenerator
    {
        private const string TableAlias = "vectorSearch";
        private readonly IVectorSearchCapabilityProvider _capabilityProvider;
        private readonly VectorSearchConfiguration _configuration;
        private readonly IVectorSearchMetricHandler _metricHandler;

        public VectorSearchQueryGenerator(IVectorSearchCapabilityProvider capabilityProvider)
            : this(capabilityProvider, new VectorSearchConfiguration())
        {
        }

        public VectorSearchQueryGenerator(
            IVectorSearchCapabilityProvider capabilityProvider,
            VectorSearchConfiguration configuration)
            : this(capabilityProvider, configuration, null)
        {
        }

        public VectorSearchQueryGenerator(
            IVectorSearchCapabilityProvider capabilityProvider,
            VectorSearchConfiguration configuration,
            IVectorSearchMetricHandler metricHandler)
        {
            _capabilityProvider = EnsureArg.IsNotNull(capabilityProvider, nameof(capabilityProvider));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _metricHandler = metricHandler;
        }

        public override Table Table => VLatest.VectorSearchParam;

        internal short EmbeddingModelId => _capabilityProvider.EmbeddingModelId;

        public override SearchParameterQueryGeneratorContext VisitVectorSearch(
            VectorSearchExpression expression,
            SearchParameterQueryGeneratorContext context)
        {
            throw new InvalidOperationException(
                $"{nameof(VectorSearchExpression)} must be generated with its predecessor CTE so distance is evaluated only for authorized candidates.");
        }

        internal void Generate(
            VectorSearchExpression expression,
            SearchParameterQueryGeneratorContext context,
            string predecessorTableExpressionName,
            SearchOptions searchOptions)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            if (expression.QueryEmbedding == null)
            {
                throw new InvalidOperationException("The vector query embedding must be populated before SQL generation.");
            }

            var embeddingParameter = context.Parameters.AddParameter(
                JsonSerializer.Serialize(expression.QueryEmbedding),
                includeInHash: true);
            object embeddingModelIdParameter = context.Parameters.AddParameter(
                VLatest.VectorSearchParam.EmbeddingModelId,
                _capabilityProvider.EmbeddingModelId,
                includeInHash: true);

            if (_configuration.EnableApproximateVectorIndex &&
                _capabilityProvider.SupportsApproximateVectorIndex &&
                predecessorTableExpressionName == null)
            {
                GenerateApproximate(expression, context, embeddingParameter, embeddingModelIdParameter, searchOptions);
                _metricHandler?.EmitQuery(new VectorSearchQueryMetricNotification { IsApproximate = true });
                return;
            }

            string distanceExpression =
                $"VECTOR_DISTANCE('cosine', {TableAlias}.Embedding, CAST({embeddingParameter} AS VECTOR(1536)))";

            context.StringBuilder.Append("SELECT ");
            if (expression.K.HasValue)
            {
                context.StringBuilder
                    .Append("TOP (")
                    .Append(context.Parameters.AddParameter(expression.K.Value, includeInHash: false))
                    .Append(") ");
            }

            context.StringBuilder
                .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(" AS T1, ")
                .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias).AppendLine(" AS Sid1,")
                .Append("MIN(").Append(distanceExpression).AppendLine(") AS VectorDistance")
                .Append("FROM ").Append(VLatest.VectorSearchParam).Append(" ").AppendLine(TableAlias);

            if (predecessorTableExpressionName != null)
            {
                context.StringBuilder
                    .Append("     JOIN ").Append(predecessorTableExpressionName).Append(" predecessor")
                    .Append(" ON ").Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(" = predecessor.T1")
                    .Append(" AND ").Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias).AppendLine(" = predecessor.Sid1");
            }

            using (var where = context.StringBuilder.BeginDelimitedWhereClause())
            {
                if (predecessorTableExpressionName == null)
                {
                    if (expression.Parameter.BaseResourceTypes?.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "A vector search without a predecessor CTE requires exactly one base resource type.");
                    }

                    where.BeginDelimitedElement()
                        .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias)
                        .Append(" = ")
                        .Append(context.Model.GetResourceTypeId(expression.Parameter.BaseResourceTypes[0]));
                }

                where.BeginDelimitedElement()
                    .Append(VLatest.VectorSearchParam.SearchParamId, TableAlias)
                    .Append(" = ")
                    .Append(context.Model.GetSearchParamId(expression.Parameter.Url));

                where.BeginDelimitedElement()
                    .Append(VLatest.VectorSearchParam.EmbeddingModelId, TableAlias)
                    .Append(" = ")
                    .Append(embeddingModelIdParameter);

                where.BeginDelimitedElement()
                    .Append(TableAlias)
                    .Append(".Embedding IS NOT NULL");
            }

            context.StringBuilder
                .Append("GROUP BY ")
                .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(", ")
                .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias).AppendLine();

            AppendHavingClause(
                expression,
                context,
                searchOptions,
                $"MIN({distanceExpression})",
                TableAlias);

            if (expression.K.HasValue)
            {
                context.StringBuilder
                    .Append("ORDER BY VectorDistance ASC, ")
                    .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(" ASC, ")
                    .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias)
                    .AppendLine(" ASC");
            }

            _metricHandler?.EmitQuery(new VectorSearchQueryMetricNotification { IsApproximate = false });
        }

        private void GenerateApproximate(
            VectorSearchExpression expression,
            SearchParameterQueryGeneratorContext context,
            object embeddingParameter,
            object embeddingModelIdParameter,
            SearchOptions searchOptions)
        {
            if (expression.Parameter.BaseResourceTypes?.Count != 1)
            {
                throw new InvalidOperationException(
                    "An approximate vector search requires exactly one base resource type.");
            }

            int resourceLimit = expression.K ?? _configuration.DefaultK;
            int approximateCandidateLimit = GetApproximateCandidateLimit(resourceLimit);
            object resourceLimitParameter = context.Parameters.AddParameter(
                resourceLimit,
                includeInHash: false);
            object approximateCandidateLimitParameter = context.Parameters.AddParameter(
                approximateCandidateLimit,
                includeInHash: false);

            context.StringBuilder
                .Append("SELECT TOP (").Append(resourceLimitParameter).Append(") ")
                .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(" AS T1, ")
                .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias).AppendLine(" AS Sid1,")
                .Append("MIN(").Append(TableAlias).AppendLine(".distance) AS VectorDistance")
                .AppendLine("FROM (")
                .Append("    SELECT TOP (").Append(approximateCandidateLimitParameter).AppendLine(") WITH APPROXIMATE")
                .Append("        ").Append(VLatest.VectorSearchParam.ResourceTypeId, "vectorEntry").AppendLine(",")
                .Append("        ").Append(VLatest.VectorSearchParam.ResourceSurrogateId, "vectorEntry").AppendLine(",")
                .AppendLine("        vectorResult.distance")
                .AppendLine("    FROM VECTOR_SEARCH(")
                .AppendLine("        TABLE = dbo.VectorSearchParam AS vectorEntry,")
                .AppendLine("        COLUMN = Embedding,")
                .Append("        SIMILAR_TO = CAST(").Append(embeddingParameter).AppendLine(" AS VECTOR(1536)),")
                .AppendLine("        METRIC = 'cosine'")
                .AppendLine("    ) AS vectorResult");

            using (var where = context.StringBuilder.BeginDelimitedWhereClause())
            {
                where.BeginDelimitedElement()
                    .Append(VLatest.VectorSearchParam.ResourceTypeId, "vectorEntry")
                    .Append(" = ")
                    .Append(context.Model.GetResourceTypeId(expression.Parameter.BaseResourceTypes[0]));

                where.BeginDelimitedElement()
                    .Append(VLatest.VectorSearchParam.SearchParamId, "vectorEntry")
                    .Append(" = ")
                    .Append(context.Model.GetSearchParamId(expression.Parameter.Url));

                where.BeginDelimitedElement()
                    .Append(VLatest.VectorSearchParam.EmbeddingModelId, "vectorEntry")
                    .Append(" = ")
                    .Append(embeddingModelIdParameter);

                where.BeginDelimitedElement()
                    .Append("vectorEntry.Embedding IS NOT NULL");
            }

            // Restricting predicates must remain inside VECTOR_SEARCH so iterative filtering finds K qualifying rows.
            // Queries with a predecessor CTE use the exact path instead; intersecting that CTE after this TOP would
            // truncate before authorization and other FHIR predicates are applied.
            // Oversampling reduces chunk crowding before rows collapse by resource, but a pathological resource with
            // many highly ranked chunks can still cause approximate search to return fewer than K unique resources.
            context.StringBuilder
                .AppendLine("    ORDER BY vectorResult.distance ASC")
                .Append(") ").AppendLine(TableAlias)
                .Append("GROUP BY ")
                .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(", ")
                .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias).AppendLine();

            AppendHavingClause(expression, context, searchOptions, $"MIN({TableAlias}.distance)", TableAlias);

            context.StringBuilder
                .Append("ORDER BY VectorDistance ASC, ")
                .Append(VLatest.VectorSearchParam.ResourceTypeId, TableAlias).Append(" ASC, ")
                .Append(VLatest.VectorSearchParam.ResourceSurrogateId, TableAlias)
                .AppendLine(" ASC");
        }

        private static void AppendHavingClause(
            VectorSearchExpression expression,
            SearchParameterQueryGeneratorContext context,
            SearchOptions searchOptions,
            string distanceExpression,
            string tableAlias)
        {
            ContinuationToken continuationToken = ContinuationToken.FromString(searchOptions.ContinuationToken);
            bool hasContinuationToken = continuationToken?.SortValue != null;
            if (!expression.MinScore.HasValue && !hasContinuationToken)
            {
                return;
            }

            context.StringBuilder.Append("HAVING ");
            bool appendAnd = false;
            if (expression.MinScore.HasValue)
            {
                double maximumDistance = 2.0 * (1.0 - expression.MinScore.Value);
                context.StringBuilder
                    .Append(distanceExpression).Append(" <= ")
                    .Append(context.Parameters.AddParameter(maximumDistance, includeInHash: true));
                appendAnd = true;
            }

            if (hasContinuationToken)
            {
                if (!double.TryParse(continuationToken.SortValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double distance) ||
                    !continuationToken.ResourceTypeId.HasValue)
                {
                    throw new InvalidOperationException("The vector search continuation token is invalid.");
                }

                object distanceParameter = context.Parameters.AddParameter(distance, includeInHash: false);
                object resourceTypeIdParameter = context.Parameters.AddParameter(continuationToken.ResourceTypeId.Value, includeInHash: false);
                object resourceSurrogateIdParameter = context.Parameters.AddParameter(continuationToken.ResourceSurrogateId, includeInHash: false);

                if (appendAnd)
                {
                    context.StringBuilder.Append(" AND ");
                }

                context.StringBuilder
                    .Append("(").Append(distanceExpression).Append(" > ").Append(distanceParameter)
                    .Append(" OR (").Append(distanceExpression).Append(" = ").Append(distanceParameter)
                    .Append(" AND (")
                    .Append(VLatest.VectorSearchParam.ResourceTypeId, tableAlias).Append(" > ").Append(resourceTypeIdParameter)
                    .Append(" OR (")
                    .Append(VLatest.VectorSearchParam.ResourceTypeId, tableAlias).Append(" = ").Append(resourceTypeIdParameter)
                    .Append(" AND ")
                    .Append(VLatest.VectorSearchParam.ResourceSurrogateId, tableAlias).Append(" > ").Append(resourceSurrogateIdParameter)
                    .Append("))))");
            }

            context.StringBuilder.AppendLine();
        }

        private int GetApproximateCandidateLimit(int resourceLimit)
        {
            double oversamplingFactor = _configuration.OversamplingFactor;
            if (double.IsNaN(oversamplingFactor) || oversamplingFactor < 1.0)
            {
                oversamplingFactor = 1.0;
            }

            double candidateLimit = Math.Ceiling(resourceLimit * oversamplingFactor);
            if (double.IsPositiveInfinity(candidateLimit) || candidateLimit >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return Math.Max(resourceLimit, (int)candidateLimit);
        }
    }
}
