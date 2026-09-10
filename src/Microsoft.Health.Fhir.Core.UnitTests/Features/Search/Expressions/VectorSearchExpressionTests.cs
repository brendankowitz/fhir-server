// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Expressions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorSearchExpressionTests
    {
        [Fact]
        public void GivenVectorSearchExpression_WhenAcceptingVisitor_ThenDispatchesToVectorSearchVisitor()
        {
            // Arrange
            var expression = CreateExpression();
            var visitor = Substitute.For<IExpressionVisitor<object, string>>();
            var context = new object();
            visitor.VisitVectorSearch(expression, context).Returns("visited");

            // Act
            string result = expression.AcceptVisitor(visitor, context);

            // Assert
            Assert.Equal("visited", result);
            visitor.Received(1).VisitVectorSearch(expression, context);
        }

        [Fact]
        public void GivenNullParameter_WhenCreatingVectorSearchExpression_ThenThrows()
        {
            // Arrange

            // Act
            Action action = () => new VectorSearchExpression(null, "query text", k: null, minScore: null);

            // Assert
            Assert.Throws<ArgumentNullException>(action);
        }

        [Fact]
        public void GivenNullQueryText_WhenCreatingVectorSearchExpression_ThenThrows()
        {
            // Arrange

            // Act
            Action action = () => new VectorSearchExpression(CreateParameter(), null, k: null, minScore: null);

            // Assert
            Assert.Throws<ArgumentNullException>(action);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void GivenWhitespaceQueryText_WhenCreatingVectorSearchExpression_ThenThrows(string queryText)
        {
            // Arrange

            // Act
            Action action = () => new VectorSearchExpression(CreateParameter(), queryText, k: null, minScore: null);

            // Assert
            Assert.Throws<ArgumentException>(action);
        }

        [Fact]
        public void GivenNullEmbedding_WhenSettingQueryEmbedding_ThenThrows()
        {
            // Arrange
            var expression = CreateExpression();

            // Act
            Action action = () => expression.SetQueryEmbedding(null);

            // Assert
            Assert.Throws<ArgumentNullException>(action);
        }

        [Fact]
        public void GivenEmbedding_WhenSettingQueryEmbedding_ThenRoundTripsValue()
        {
            // Arrange
            var expression = CreateExpression();
            var embedding = new[] { 0.25f, 0.5f };

            // Act
            expression.SetQueryEmbedding(embedding);

            // Assert
            Assert.Same(embedding, expression.QueryEmbedding);
        }

        [Fact]
        public void GivenVectorSearchExpression_WhenConvertingToString_ThenDoesNotContainQueryText()
        {
            // Arrange
            const string queryText = "patient clinical free text";
            var expression = new VectorSearchExpression(CreateParameter(), queryText, k: 10, minScore: 0.7);

            // Act
            string result = expression.ToString();

            // Assert
            Assert.DoesNotContain(queryText, result, StringComparison.Ordinal);
        }

        [Fact]
        public void GivenExpressionsDifferingOnlyInQueryText_WhenComputingValueInsensitiveHash_ThenHashesMatchUnlessParameterUrlDiffers()
        {
            // Arrange
            var parameter = CreateParameter();
            var firstExpression = new VectorSearchExpression(parameter, "first query", k: 10, minScore: 0.7);
            var secondExpression = new VectorSearchExpression(parameter, "second query", k: 10, minScore: 0.7);
            var differentParameterExpression = new VectorSearchExpression(
                new SearchParameterInfo("embedding", "embedding", SearchParamType.String, new Uri("http://hl7.org/fhir/SearchParameter/other-embedding")),
                "first query",
                k: 10,
                minScore: 0.7);

            // Act
            int firstHash = GetValueInsensitiveHashCode(firstExpression);
            int secondHash = GetValueInsensitiveHashCode(secondExpression);
            int differentParameterHash = GetValueInsensitiveHashCode(differentParameterExpression);

            // Assert
            Assert.Equal(firstHash, secondHash);
            Assert.NotEqual(firstHash, differentParameterHash);
        }

        private static VectorSearchExpression CreateExpression()
        {
            return new VectorSearchExpression(CreateParameter(), "query text", k: 10, minScore: 0.7);
        }

        private static SearchParameterInfo CreateParameter()
        {
            return new SearchParameterInfo("embedding", "embedding", SearchParamType.String, new Uri("http://hl7.org/fhir/SearchParameter/embedding"));
        }

        private static int GetValueInsensitiveHashCode(Expression expression)
        {
            HashCode hashCode = default;
            expression.AddValueInsensitiveHashCode(ref hashCode);
            return hashCode.ToHashCode();
        }
    }
}
