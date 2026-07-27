// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Expression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    /// <summary>
    /// Unit tests for <see cref="IgnixaExpressionCapabilityChecker"/>, the semantic gate that keeps
    /// token and composite search predicates on the legacy SQL path while the Ignixa compiler cannot
    /// emit correct SQL for them.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaExpressionCapabilityCheckerTests
    {
        [Fact]
        public void GivenNullExpression_WhenChecked_ThenSupported()
        {
            Assert.True(IgnixaExpressionCapabilityChecker.IsSupported(null));
        }

        [Fact]
        public void GivenStringSearchParameter_WhenChecked_ThenSupported()
        {
            Expression expression = CreateSearchParameter("address-city", SearchParamType.String);

            Assert.True(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenTokenSearchParameter_WhenChecked_ThenNotSupported()
        {
            Expression expression = CreateSearchParameter("identifier", SearchParamType.Token);

            Assert.False(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenCompositeSearchParameter_WhenChecked_ThenNotSupported()
        {
            Expression expression = CreateSearchParameter("code-value-quantity", SearchParamType.Composite);

            Assert.False(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenMissingTokenSearchParameter_WhenChecked_ThenNotSupported()
        {
            var parameter = new SearchParameterInfo("identifier", "identifier", SearchParamType.Token);
            Expression expression = Expression.MissingSearchParameter(parameter, isMissing: true);

            Assert.False(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenAndOfStringParameters_WhenChecked_ThenSupported()
        {
            Expression expression = Expression.And(
                CreateSearchParameter("address-city", SearchParamType.String),
                CreateSearchParameter("address-state", SearchParamType.String));

            Assert.True(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenAndMixingStringAndToken_WhenChecked_ThenNotSupported()
        {
            Expression expression = Expression.And(
                CreateSearchParameter("address-city", SearchParamType.String),
                CreateSearchParameter("identifier", SearchParamType.Token));

            Assert.False(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenResourceTypeTokenParameter_WhenChecked_ThenSupported()
        {
            // _type is a Token-typed parameter, but it is a structural resource-type restriction that Ignixa
            // lowers natively onto the resource table, so it must not defer the query to the legacy path.
            Expression expression = CreateSearchParameter(SearchParameterNames.ResourceType, SearchParamType.Token);

            Assert.True(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenAndOfResourceTypeAndStringParameters_WhenChecked_ThenSupported()
        {
            Expression expression = Expression.And(
                CreateSearchParameter(SearchParameterNames.ResourceType, SearchParamType.Token),
                CreateSearchParameter("address-city", SearchParamType.String));

            Assert.True(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        [Fact]
        public void GivenAndOfResourceTypeAndUserTokenParameters_WhenChecked_ThenNotSupported()
        {
            Expression expression = Expression.And(
                CreateSearchParameter(SearchParameterNames.ResourceType, SearchParamType.Token),
                CreateSearchParameter("gender", SearchParamType.Token));

            Assert.False(IgnixaExpressionCapabilityChecker.IsSupported(expression));
        }

        private static SearchParameterExpression CreateSearchParameter(string code, SearchParamType type)
        {
            var parameter = new SearchParameterInfo(code, code, type);
            Expression inner = Expression.StringEquals(FieldName.String, componentIndex: null, value: "value", ignoreCase: true);
            return Expression.SearchParameter(parameter, inner);
        }
    }
}
