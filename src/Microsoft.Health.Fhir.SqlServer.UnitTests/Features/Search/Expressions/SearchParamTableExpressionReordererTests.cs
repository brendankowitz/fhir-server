// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors.QueryGenerators;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Expressions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SearchParamTableExpressionReordererTests
    {
        private static readonly SearchParameterExpression NormalExpression = new SearchParameterExpression(new SearchParameterInfo("TestParam", "TestParam"), Expression.Equals(FieldName.TokenCode, null, "TestValue"));
        private static readonly SearchParameterExpression NotExpression = new SearchParameterExpression(NormalExpression.Parameter, Expression.Not(NormalExpression.Expression));

        [Fact]
        public void GivenExpressionWithSingleTableExpression_WhenReordered_ReturnsOriginalExpression()
        {
            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.Normal));
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Equal(inputExpression, visitedExpression);
        }

        [Fact]
        public void GivenExpressionWithMultipleTableExpressions_WhenReordered_DenormilizedExpressionReturnedFirst()
        {
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(new ReferenceQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.All),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Collection(visitedExpression.SearchParamTableExpressions, new[] { 1, 0 }.Select<int, Action<SearchParamTableExpression>>(x => e => Assert.Equal(tableExpressions[x], e)).ToArray());
        }

        [Fact]
        public void GivenExpressionWithMultipleTableExpressions_WhenReordered_ReferenceExpressionReturnedBeforeNormal()
        {
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(null, NormalExpression, SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(new ReferenceQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Normal),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Collection(visitedExpression.SearchParamTableExpressions, new[] { 1, 0 }.Select<int, Action<SearchParamTableExpression>>(x => e => Assert.Equal(tableExpressions[x], e)).ToArray());
        }

        [Fact]
        [Trait(Traits.Category, Categories.CompartmentSearch)]
        public void GivenExpressionWithMultipleTableExpressions_WhenReordered_CompartmentExpressionReturnedBeforeNormal()
        {
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(null, NormalExpression, SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(new CompartmentQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Normal),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Collection(visitedExpression.SearchParamTableExpressions, new[] { 1, 0 }.Select<int, Action<SearchParamTableExpression>>(x => e => Assert.Equal(tableExpressions[x], e)).ToArray());
        }

        [Fact]
        public void GivenExpressionWithMultipleTableExpressions_WhenReordered_MissingParameterExpressionReturnedBeforeNotExpression()
        {
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(null, NotExpression, SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(null, new MissingSearchParameterExpression(new SearchParameterInfo("TestParam", "TestParam"), true), SearchParamTableExpressionKind.Normal),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Collection(visitedExpression.SearchParamTableExpressions, new[] { 1, 0 }.Select<int, Action<SearchParamTableExpression>>(x => e => Assert.Equal(tableExpressions[x], e)).ToArray());
        }

        [Fact]
        public void GivenExpressionWithMultipleTableExpressions_WhenReordered_IncludeExpressionReturnedLast()
        {
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(new IncludeQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Include),
                new SearchParamTableExpression(null, NormalExpression, SearchParamTableExpressionKind.Normal),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);
            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);
            Assert.Collection(visitedExpression.SearchParamTableExpressions, new[] { 1, 0 }.Select<int, Action<SearchParamTableExpression>>(x => e => Assert.Equal(tableExpressions[x], e)).ToArray());
        }

        [Fact]
        [Trait(Traits.Category, Categories.CompartmentSearch)]
        public void GivenVectorAndRestrictingExpressions_WhenReordered_VectorRunsAfterAllFiltersAndBeforeInclude()
        {
            var vectorExpression = new VectorSearchExpression(
                new SearchParameterInfo("vector", "vector"),
                "query",
                k: null,
                minScore: null);
            var tableExpressions = new List<SearchParamTableExpression>
            {
                new SearchParamTableExpression(new IncludeQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Include),
                new SearchParamTableExpression(
                    new VectorSearchQueryGenerator(Substitute.For<IVectorSearchCapabilityProvider>()),
                    vectorExpression,
                    SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(null, NotExpression, SearchParamTableExpressionKind.Normal),
                new SearchParamTableExpression(new CompartmentQueryGenerator(), NormalExpression, SearchParamTableExpressionKind.Normal),
            };

            var inputExpression = SqlRootExpression.WithSearchParamTableExpressions(tableExpressions);

            var visitedExpression = (SqlRootExpression)inputExpression.AcceptVisitor(SearchParamTableExpressionReorderer.Instance);

            Assert.Collection(
                visitedExpression.SearchParamTableExpressions,
                expression => Assert.Equal(tableExpressions[3], expression),
                expression => Assert.Equal(tableExpressions[2], expression),
                expression => Assert.Equal(tableExpressions[1], expression),
                expression => Assert.Equal(tableExpressions[0], expression));
        }
    }
}
