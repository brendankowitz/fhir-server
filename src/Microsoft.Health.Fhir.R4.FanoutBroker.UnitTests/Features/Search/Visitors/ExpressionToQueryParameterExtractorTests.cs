// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;
using Microsoft.Health.Fhir.ValueSets;
using Xunit;
using SearchParamExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests.Features.Search.Visitors
{
    public class ExpressionToQueryParameterExtractorTests
    {
        [Fact]
        public void ChainedSearchExpression_ShouldGenerateCorrectFHIRSyntax()
        {
            // Arrange
            var extractor = new ExpressionToQueryParameterExtractor();

            // Create the nested search parameter expression for "name" parameter
            var nameSearchParam = new SearchParameterInfo("name", "name", SearchParamType.String, null, null, "Patient.name", null);
            var nameStringExpr = SearchParamExpression.StartsWith(FieldName.TokenCode, 0, "Sarah", false);
            var nameSearchParamExpr = SearchParamExpression.SearchParameter(nameSearchParam, nameStringExpr);

            // Create the chained expression for "subject:Patient.name"
            var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "DiagnosticReport.subject", null);
            var chainedExpr = SearchParamExpression.Chained(
                new[] { "DiagnosticReport" },
                subjectSearchParam,
                new[] { "Patient" },
                false,
                nameSearchParamExpr);

            // Act
            chainedExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should generate exactly one parameter: subject:Patient.name=Sarah
            Assert.Single(parameters);
            Assert.Equal("subject:Patient.name", parameters[0].Item1);
            Assert.Equal("Sarah*", parameters[0].Item2);

            // Should NOT generate any _type parameters
            Assert.DoesNotContain(parameters, p => p.Item1 == "_type");

            // Should NOT generate separate name parameters
            Assert.DoesNotContain(parameters, p => p.Item1 == "name");
        }

        [Fact]
        public void ChainedSearchExpression_ShouldNotGenerateDuplicateParameters()
        {
            // Arrange
            var extractor = new ExpressionToQueryParameterExtractor();

            // Create the same chained expression twice to test deduplication
            var nameSearchParam = new SearchParameterInfo("name", "name", SearchParamType.String, null, null, "Patient.name", null);
            var nameStringExpr = SearchParamExpression.StartsWith(FieldName.TokenCode, 0, "Sarah", false);
            var nameSearchParamExpr = SearchParamExpression.SearchParameter(nameSearchParam, nameStringExpr);

            var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "DiagnosticReport.subject", null);
            var chainedExpr1 = SearchParamExpression.Chained(
                new[] { "DiagnosticReport" },
                subjectSearchParam,
                new[] { "Patient" },
                false,
                nameSearchParamExpr);

            var chainedExpr2 = SearchParamExpression.Chained(
                new[] { "DiagnosticReport" },
                subjectSearchParam,
                new[] { "Patient" },
                false,
                nameSearchParamExpr);

            // Act - visit the same expression twice
            chainedExpr1.AcceptVisitor(extractor, null);
            chainedExpr2.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should still generate exactly one parameter, not duplicates
            Assert.Single(parameters);
            Assert.Equal("subject:Patient.name", parameters[0].Item1);
            Assert.Equal("Sarah*", parameters[0].Item2);
        }

        [Fact]
        public void ResourceTypes_ShouldNotGenerateTypeParameterForSingleResourceType()
        {
            // Arrange
            var extractor = new ExpressionToQueryParameterExtractor();

            var nameSearchParam = new SearchParameterInfo("name", "name", SearchParamType.String, null, null, "Patient.name", null);
            var nameStringExpr = SearchParamExpression.StartsWith(FieldName.TokenCode, 0, "Sarah", false);
            var nameSearchParamExpr = SearchParamExpression.SearchParameter(nameSearchParam, nameStringExpr);

            var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "DiagnosticReport.subject", null);
            var chainedExpr = SearchParamExpression.Chained(
                new[] { "DiagnosticReport" }, // Single resource type
                subjectSearchParam,
                new[] { "Patient" },
                false,
                nameSearchParamExpr);

            // Act
            chainedExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should NOT generate _type parameter for single resource type
            Assert.DoesNotContain(parameters, p => p.Item1 == "_type");
        }

        [Fact]
        public void SimpleSearchParameter_ShouldGenerateCorrectParameter()
        {
            // Arrange
            var extractor = new ExpressionToQueryParameterExtractor();
            var searchParam = new SearchParameterInfo("name", "name", SearchParamType.String, null, null, "Patient.name", null);
            var stringExpr = SearchParamExpression.StringEquals(FieldName.TokenCode, 0, "John", false);
            var searchParamExpr = SearchParamExpression.SearchParameter(searchParam, stringExpr);

            // Act
            searchParamExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;
            Assert.Single(parameters);
            Assert.Equal("name", parameters[0].Item1);
            Assert.Equal("John", parameters[0].Item2);
        }

        [Fact]
        public void ResourceSpecificEndpoint_ShouldNotGenerateTypeParameter()
        {
            // Arrange - Simulate a request to /Patient?address:contains=Meadow
            var extractor = new ExpressionToQueryParameterExtractor("Patient"); // Context resource type specified

            var addressSearchParam = new SearchParameterInfo("address", "address", SearchParamType.String, null, null, "Patient.address", null);
            var containsExpr = SearchParamExpression.Contains(FieldName.TokenCode, 0, "Meadow", false);
            var searchParamExpr = SearchParamExpression.SearchParameter(addressSearchParam, containsExpr);

            // Act
            searchParamExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should generate the address parameter with contains modifier
            Assert.Single(parameters);
            Assert.Equal("address:contains", parameters[0].Item1);
            Assert.Equal("Meadow", parameters[0].Item2);

            // Should NOT generate any _type parameters since we have a context resource type
            Assert.DoesNotContain(parameters, p => p.Item1 == "_type");
        }

        [Fact]
        public void SystemLevelSearch_ShouldGenerateTypeParameterForMultipleTypes()
        {
            // Arrange - Simulate a system-level search with multiple resource types
            var extractor = new ExpressionToQueryParameterExtractor(); // No context resource type

            // Create expressions that would normally generate multiple resource types
            var nameSearchParam = new SearchParameterInfo("name", "name", SearchParamType.String, null, null, "name", null);
            var nameExpr = SearchParamExpression.StringEquals(FieldName.TokenCode, 0, "test", false);
            var nameSearchParamExpr = SearchParamExpression.SearchParameter(nameSearchParam, nameExpr);

            var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "subject", null);
            var chainedExpr = SearchParamExpression.Chained(
                new[] { "DiagnosticReport", "Observation" }, // Multiple resource types
                subjectSearchParam,
                new[] { "Patient" },
                false,
                nameSearchParamExpr);

            // Act
            chainedExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should generate _type parameter for multiple resource types in system-level search
            var typeParam = parameters.FirstOrDefault(p => p.Item1 == "_type");
            Assert.NotNull(typeParam);
            Assert.Contains("DiagnosticReport", typeParam.Item2);
            Assert.Contains("Observation", typeParam.Item2);
        }

        [Fact]
        public void ReferenceSearchParameter_ShouldPreserveFullReferenceValue()
        {
            // Arrange - Reproduce the issue: /Encounter?subject=Patient/searchpatient3
            var extractor = new ExpressionToQueryParameterExtractor("Encounter");

            var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "Encounter.subject", null);

            // Create a reference expression that should preserve "Patient/searchpatient3"
            var referenceExpr = SearchParamExpression.StringEquals(FieldName.ReferenceResourceId, 0, "Patient/searchpatient3", false);
            var searchParamExpr = SearchParamExpression.SearchParameter(subjectSearchParam, referenceExpr);

            // Act
            searchParamExpr.AcceptVisitor(extractor, null);

            // Assert
            var parameters = extractor.QueryParameters;

            // Should generate exactly one parameter
            Assert.Single(parameters);
            Assert.Equal("subject", parameters[0].Item1);

            // The value should be the full reference "Patient/searchpatient3", not truncated to "Patient"
            Assert.Equal("Patient/searchpatient3", parameters[0].Item2);
        }

        [Fact]
        public void ReferenceSearchParameter_WithDifferentExpressionTypes_ShouldPreserveFullValue()
        {
            // Test various expression types that might contain reference values
            var testCases = new[]
            {
                ("Patient/test123", "Patient/test123"),
                ("Organization/org456", "Organization/org456"),
                ("Practitioner/prac789", "Practitioner/prac789"),
                ("Device/device001", "Device/device001"),
            };

            foreach (var (input, expected) in testCases)
            {
                // Arrange
                var extractor = new ExpressionToQueryParameterExtractor("Encounter");
                var subjectSearchParam = new SearchParameterInfo("subject", "subject", SearchParamType.Reference, null, null, "Encounter.subject", null);

                var referenceExpr = SearchParamExpression.StringEquals(FieldName.ReferenceResourceId, 0, input, false);
                var searchParamExpr = SearchParamExpression.SearchParameter(subjectSearchParam, referenceExpr);

                // Act
                searchParamExpr.AcceptVisitor(extractor, null);

                // Assert
                var parameters = extractor.QueryParameters;
                Assert.Single(parameters);
                Assert.Equal("subject", parameters[0].Item1);
                Assert.Equal(expected, parameters[0].Item2);
            }
        }
    }
}
