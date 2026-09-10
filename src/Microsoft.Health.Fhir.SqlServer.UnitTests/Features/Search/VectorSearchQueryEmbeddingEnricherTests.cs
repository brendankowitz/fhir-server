// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorSearchQueryEmbeddingEnricherTests
    {
        private readonly IEmbeddingClient _embeddingClient = Substitute.For<IEmbeddingClient>();
        private readonly IVectorSearchMetricHandler _metricHandler = Substitute.For<IVectorSearchMetricHandler>();

        [Fact]
        public async Task GivenExpressionWithoutVectorSearch_WhenEnriching_ThenDoesNotCallEmbeddingClient()
        {
            // Arrange
            Expression expression = Expression.SearchParameter(
                CreateParameter(),
                Expression.StringEquals(FieldName.TokenCode, null, "example", ignoreCase: false));

            // Act
            await SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(expression, _embeddingClient, _metricHandler, CancellationToken.None);

            // Assert
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenVectorSearchExpression_WhenEnriching_ThenAppliesEmbeddingFromOneFullBudgetCall()
        {
            // Arrange
            VectorSearchExpression vectorSearch = CreateVectorSearchExpression("first query");
            float[] embedding = { 0.1f, 0.2f };
            _embeddingClient.EmbedAsync(default, default, default)
                .ReturnsForAnyArgs(new[] { embedding });

            // Act
            await SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(
                Expression.SearchParameter(vectorSearch.Parameter, vectorSearch),
                _embeddingClient,
                _metricHandler,
                CancellationToken.None);

            // Assert
            Assert.Same(embedding, vectorSearch.QueryEmbedding);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first query" })),
                false,
                CancellationToken.None);
            _metricHandler.Received(1).EmitEmbedding(
                Arg.Is<VectorEmbeddingMetricNotification>(notification =>
                    notification.Operation == VectorEmbeddingOperation.Read &&
                    notification.Outcome == VectorEmbeddingOutcome.Success &&
                    notification.ElapsedMilliseconds >= 0));
        }

        [Fact]
        public async Task GivenVectorSearchExpression_WhenEnriching_ThenRequestsFullEmbeddingBudget()
        {
            // Arrange
            VectorSearchExpression vectorSearch = CreateVectorSearchExpression("query");
            _embeddingClient.EmbedAsync(default, default, default)
                .ReturnsForAnyArgs(new[] { new[] { 0.1f } });

            // Act
            await SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(
                Expression.SearchParameter(vectorSearch.Parameter, vectorSearch),
                _embeddingClient,
                _metricHandler,
                CancellationToken.None);

            // Assert
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Any<IReadOnlyList<string>>(),
                false,
                CancellationToken.None);
        }

        [Fact]
        public async Task GivenTwoVectorSearchExpressions_WhenEnriching_ThenAppliesEachEmbeddingFromOneOrderedCall()
        {
            // Arrange
            VectorSearchExpression first = CreateVectorSearchExpression("first query");
            VectorSearchExpression second = CreateVectorSearchExpression("second query");
            float[] firstEmbedding = { 0.1f };
            float[] secondEmbedding = { 0.2f };
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { firstEmbedding, secondEmbedding });
            Expression expression = Expression.And(
                Expression.SearchParameter(first.Parameter, first),
                Expression.SearchParameter(second.Parameter, second));

            // Act
            await SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(expression, _embeddingClient, _metricHandler, CancellationToken.None);

            // Assert
            Assert.Same(firstEmbedding, first.QueryEmbedding);
            Assert.Same(secondEmbedding, second.QueryEmbedding);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first query", "second query" })),
                false,
                CancellationToken.None);
        }

        [Fact]
        public async Task GivenEmbeddingClientUnavailable_WhenEnriching_ThenExceptionPropagates()
        {
            // Arrange
            VectorSearchExpression vectorSearch = CreateVectorSearchExpression("query");
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new EmbeddingUnavailableException());

            // Act
            Func<Task> act = () => SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(
                Expression.SearchParameter(vectorSearch.Parameter, vectorSearch),
                _embeddingClient,
                _metricHandler,
                CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<EmbeddingUnavailableException>(act);
            _metricHandler.Received(1).EmitEmbedding(
                Arg.Is<VectorEmbeddingMetricNotification>(notification =>
                    notification.Operation == VectorEmbeddingOperation.Read &&
                    notification.Outcome == VectorEmbeddingOutcome.Failure &&
                    notification.ElapsedMilliseconds >= 0));
        }

        [Fact]
        public async Task GivenEmbeddingCountDoesNotMatchVectorExpressionCount_WhenEnriching_ThenThrows()
        {
            // Arrange
            VectorSearchExpression first = CreateVectorSearchExpression("first query");
            VectorSearchExpression second = CreateVectorSearchExpression("second query");
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { new[] { 0.1f } });
            Expression expression = Expression.And(
                Expression.SearchParameter(first.Parameter, first),
                Expression.SearchParameter(second.Parameter, second));

            // Act
            Func<Task> act = () => SqlServerSearchService.VectorSearchQueryEmbeddingEnricher.EnrichAsync(expression, _embeddingClient, _metricHandler, CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<InvalidOperationException>(act);
            Assert.Null(first.QueryEmbedding);
            Assert.Null(second.QueryEmbedding);
        }

        private static VectorSearchExpression CreateVectorSearchExpression(string queryText)
        {
            return new VectorSearchExpression(CreateParameter(), queryText, k: 10, minScore: null);
        }

        private static SearchParameterInfo CreateParameter()
        {
            return new SearchParameterInfo(
                "embedding",
                "embedding",
                SearchParamType.String,
                new Uri("http://hl7.org/fhir/SearchParameter/embedding"));
        }
    }
}
