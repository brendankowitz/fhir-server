// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Resources
{
    /// <summary>
    /// Parity tests: the Ignixa node-backed resolver and the Firely-POCO resolver must reach
    /// identical outcomes for the same transaction-bundle-shaped corpus, because they share the
    /// same decision logic (<see cref="ResourceReferenceResolver.TryResolveReferenceValueAsync"/>) --
    /// only the traversal mechanism differs (schema-typed <c>IElement</c> tree vs. Firely POCO graph).
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.DomainLogicValidation)]
    public class IgnixaResourceReferenceResolverTests
    {
        private const string PatientUrnPlaceholder = "urn:uuid:aaaaaaaa-0000-0000-0000-000000000001";
        private const string OrganizationUrnPlaceholder = "urn:uuid:aaaaaaaa-0000-0000-0000-000000000002";
        private const string ConditionalPractitionerReference = "Practitioner?identifier=http://example.org|999";

        private readonly ISearchService _searchService = Substitute.For<ISearchService>();
        private readonly ResourceReferenceResolver _coreResolver;
        private readonly IgnixaResourceReferenceResolver _ignixaResolver;
        private readonly IIgnixaJsonSerializer _ignixaSerializer = new IgnixaJsonSerializer();
        private readonly IIgnixaSchemaContext _schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
        private readonly FhirJsonParser _firelyParser = new();

        // resourceType, json, expected resolved count
        //
        // The first corpus entry (PlanDefinition-anchored) is version-gated: it exercises both a real
        // reference-resolution path (a urn:uuid: placeholder plus a nested Identifier.assigner
        // reference, proving recursion into Reference.identifier.assigner) AND a "decoy" -- a field
        // literally named "reference" that is NOT itself a Reference datatype, to prove neither the
        // Firely nor the Ignixa path spuriously rewrites it (see IgnixaReferenceScanner's remarks).
        // The R4+ decoy uses the `Expression` datatype's `reference` (uri) property, but `Expression`
        // was introduced in R4 and doesn't exist in STU3's model at all -- Firely's STU3 POCO parser
        // throws FormatException ("unknown datatype Expression") on it. STU3's equivalent decoy is
        // `DetectedIssue.reference` (also a plain uri, JSON key "reference"), embedded as a contained
        // resource; STU3's `PlanDefinition` also has no `subject` element and its
        // `action.condition.expression` is a plain string (not the `Expression` complex type), so the
        // real-reference piece is carried by a `valueReference` extension instead of `subjectReference`.
        private static readonly (string ResourceType, string Json, int ExpectedResolvedCount)[] Corpus =
        {
#if !Stu3
            (
                "PlanDefinition",
                $$"""
                {
                    "resourceType": "PlanDefinition",
                    "id": "pd1",
                    "status": "draft",
                    "extension": [
                        {
                            "url": "http://example.org/fhir/StructureDefinition/some-expression-ext",
                            "valueExpression": {
                                "language": "text/fhirpath",
                                "expression": "true",
                                "reference": "http://example.org/expr/99"
                            }
                        }
                    ],
                    "subjectReference": {
                        "reference": "{{PatientUrnPlaceholder}}",
                        "identifier": {
                            "system": "http://example.org/identifiers",
                            "value": "12345",
                            "assigner": {
                                "reference": "{{OrganizationUrnPlaceholder}}"
                            }
                        }
                    },
                    "action": [
                        {
                            "condition": [
                                {
                                    "kind": "applicability",
                                    "expression": {
                                        "language": "text/fhirpath",
                                        "expression": "true",
                                        "reference": "http://example.org/expr/42"
                                    }
                                }
                            ]
                        }
                    ]
                }
                """,
                2),
#else
            (
                "PlanDefinition",
                $$"""
                {
                    "resourceType": "PlanDefinition",
                    "id": "pd1",
                    "status": "draft",
                    "contained": [
                        {
                            "resourceType": "DetectedIssue",
                            "id": "di1",
                            "status": "preliminary",
                            "reference": "http://example.org/expr/99"
                        }
                    ],
                    "extension": [
                        {
                            "url": "http://example.org/fhir/StructureDefinition/some-reference-ext",
                            "valueReference": {
                                "reference": "{{PatientUrnPlaceholder}}",
                                "identifier": {
                                    "system": "http://example.org/identifiers",
                                    "value": "12345",
                                    "assigner": {
                                        "reference": "{{OrganizationUrnPlaceholder}}"
                                    }
                                }
                            }
                        }
                    ],
                    "action": [
                        {
                            "condition": [
                                {
                                    "kind": "applicability",
                                    "expression": "true"
                                }
                            ]
                        }
                    ]
                }
                """,
                2),
#endif

            // Immunization.occurrenceDateTime is also an R4+ addition (the choice-typed occurrence[x]
            // element replaced STU3's plain `date` dateTime); STU3's Immunization additionally requires
            // a `notGiven` boolean that R4+ dropped in favor of a `status` code value.
#if !Stu3
            (
                "Immunization",
                $$"""
                {
                    "resourceType": "Immunization",
                    "id": "imm1",
                    "status": "completed",
                    "vaccineCode": { "text": "Test vaccine" },
                    "patient": { "reference": "{{PatientUrnPlaceholder}}" },
                    "occurrenceDateTime": "2024-01-01"
                }
                """,
                1),
#else
            (
                "Immunization",
                $$"""
                {
                    "resourceType": "Immunization",
                    "id": "imm1",
                    "status": "completed",
                    "notGiven": false,
                    "vaccineCode": { "text": "Test vaccine" },
                    "patient": { "reference": "{{PatientUrnPlaceholder}}" },
                    "date": "2024-01-01"
                }
                """,
                1),
#endif
            (
                "Observation",
                $$"""
                {
                    "resourceType": "Observation",
                    "id": "obs1",
                    "status": "final",
                    "code": { "text": "test" },
                    "subject": { "reference": "{{PatientUrnPlaceholder}}" },
                    "performer": [
                        { "display": "Dr. Nobody" }
                    ],
                    "extension": [
                        {
                            "url": "http://example.org/fhir/StructureDefinition/performer-org",
                            "valueReference": { "reference": "{{OrganizationUrnPlaceholder}}" }
                        }
                    ],
                    "contained": [
                        {
                            "resourceType": "RelatedPerson",
                            "id": "rp1",
                            "patient": { "reference": "Patient/patient-1" }
                        }
                    ]
                }
                """,
                2),
            (
                "Patient",
                $$"""
                {
                    "resourceType": "Patient",
                    "id": "pat2",
                    "generalPractitioner": [
                        { "reference": "{{ConditionalPractitionerReference}}" }
                    ]
                }
                """,
                1),
        };

        public IgnixaResourceReferenceResolverTests()
        {
            _coreResolver = new ResourceReferenceResolver(_searchService, new TestQueryStringParser(), Substitute.For<ILogger<ResourceReferenceResolver>>());
            _ignixaResolver = new IgnixaResourceReferenceResolver(_coreResolver, _schemaContext);

            SearchResultEntry practitionerMatch = GetMockSearchEntry("prac-999", "Practitioner");
            var searchResult = new SearchResult(new[] { practitionerMatch }, null, null, Array.Empty<Tuple<string, string>>());
            _searchService.SearchAsync("Practitioner", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), CancellationToken.None).Returns(searchResult);
        }

        [Fact]
        public async Task GivenTransactionBundleShapedCorpus_WhenResolvedByBothPaths_ThenResultsAreIdentical()
        {
            var firelyDictionary = CreateSeededDictionary();
            var ignixaDictionary = CreateSeededDictionary();

            foreach (var (resourceType, json, expectedResolvedCount) in Corpus)
            {
                Resource firelyResource = _firelyParser.Parse<Resource>(json);
                int firelyResolvedCount = await _coreResolver.ResolveReferencesAsync(firelyResource, firelyDictionary, requestUrl: null, CancellationToken.None);

                var ignixaResource = _ignixaSerializer.Parse(json);
                int ignixaResolvedCount = await _ignixaResolver.ResolveReferencesAsync(ignixaResource, ignixaDictionary, requestUrl: null, CancellationToken.None);

                Assert.True(
                    expectedResolvedCount == firelyResolvedCount,
                    $"{resourceType}: expected {expectedResolvedCount} resolved references via Firely path, got {firelyResolvedCount}");
                Assert.True(
                    firelyResolvedCount == ignixaResolvedCount,
                    $"{resourceType}: resolved-count parity mismatch (Firely={firelyResolvedCount}, Ignixa={ignixaResolvedCount})");

                string firelyJson = firelyResource.ToJson();
                string ignixaJson = _ignixaSerializer.Serialize(ignixaResource);

                JsonNode firelyNode = JsonNode.Parse(firelyJson)!;
                JsonNode ignixaNode = JsonNode.Parse(ignixaJson)!;

                Assert.True(
                    JsonNode.DeepEquals(firelyNode, ignixaNode),
                    $"{resourceType}: serialized entry mismatch.\nFirely: {firelyJson}\nIgnixa: {ignixaJson}");
            }

            Assert.Equal(firelyDictionary.Count, ignixaDictionary.Count);
            foreach (var (key, value) in firelyDictionary)
            {
                Assert.True(ignixaDictionary.TryGetValue(key, out var ignixaValue), $"Missing dictionary key '{key}' on the Ignixa path");
                Assert.Equal(value, ignixaValue);
            }

            Assert.True(firelyDictionary.ContainsKey(ConditionalPractitionerReference));
        }

        private static IDictionary<string, (string resourceId, string resourceType)> CreateSeededDictionary()
        {
            return new Dictionary<string, (string resourceId, string resourceType)>
            {
                [PatientUrnPlaceholder] = ("patient-1", "Patient"),
                [OrganizationUrnPlaceholder] = ("org-1", "Organization"),
            };
        }

        private static SearchResultEntry GetMockSearchEntry(string resourceId, string resourceType)
        {
            return new SearchResultEntry(
               new ResourceWrapper(
                   resourceId,
                   "1",
                   resourceType,
                   new RawResource("data", FhirResourceFormat.Json, isMetaSet: false),
                   null,
                   DateTimeOffset.MinValue,
                   false,
                   null,
                   null,
                   null));
        }
    }
}
