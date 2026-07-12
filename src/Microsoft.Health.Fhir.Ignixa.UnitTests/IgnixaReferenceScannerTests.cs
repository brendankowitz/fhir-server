// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.Generated;
using Xunit;

namespace Microsoft.Health.Fhir.Ignixa.UnitTests;

/// <summary>
/// Covers the disambiguation matrix from the design review: elements named "reference" that are
/// NOT the Reference datatype (must NOT be yielded) versus Reference-typed elements under any name,
/// including choice types, extensions, contained resources, and nested references.
/// </summary>
public class IgnixaReferenceScannerTests
{
    private readonly IIgnixaJsonSerializer _serializer = new IgnixaJsonSerializer();
    private readonly R4CoreSchemaProvider _r4Schema = new();
    private readonly R5CoreSchemaProvider _r5Schema = new();

    // Covers: Expression.reference (top-level extension AND action.condition.expression) NOT yielded;
    // choice-typed subjectReference yielded; Reference.identifier.assigner (nested Reference) yielded;
    // a display-only Reference (valueReference with only "display") yielded as a handle with no
    // resolvable value.
    private const string PlanDefinitionAdversarialJson = """
        {
            "resourceType": "PlanDefinition",
            "id": "adversarial-1",
            "status": "draft",
            "extension": [
                {
                    "url": "http://example.org/fhir/StructureDefinition/some-expression-ext",
                    "valueExpression": {
                        "language": "text/fhirpath",
                        "expression": "true",
                        "reference": "http://example.org/expr/ext99"
                    }
                },
                {
                    "url": "http://example.org/fhir/StructureDefinition/display-only-ref-ext",
                    "valueReference": {
                        "display": "Unnamed Organization"
                    }
                }
            ],
            "subjectReference": {
                "reference": "Practitioner/prac1",
                "_reference": {
                    "extension": [
                        {
                            "url": "http://example.org/fhir/StructureDefinition/shadow-marker",
                            "valueString": "shadow-preserved"
                        }
                    ]
                },
                "identifier": {
                    "system": "http://example.org/identifiers",
                    "value": "12345",
                    "assigner": {
                        "reference": "Organization/org2"
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
                                "reference": "http://example.org/expr/cond42"
                            }
                        }
                    ]
                }
            ]
        }
        """;

    [Fact]
    public void GivenPlanDefinitionAdversarialCorpus_WhenScanned_ThenOnlyTrueReferencesAreYielded()
    {
        var root = ParseToElement(PlanDefinitionAdversarialJson, _r4Schema);

        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();
        var values = handles.Select(h => h.Reference).ToList();

        // subjectReference, its nested identifier.assigner, and the display-only extension value --
        // three Reference-typed elements total. Neither Expression.reference (top-level extension or
        // action.condition.expression) is a Reference element and neither is yielded.
        //
        // This count assertion is also the guard-dependent tripwire for the IgnixaReferenceScanner
        // phantom-element workaround (mutation-verified: fails if `!element.HasPrimitiveValue` is
        // removed from the scanner, because the phantom "reference" leaf under subjectReference would
        // then be yielded as a fourth, null-valued handle). See
        // GivenReferenceWithPrimitiveExtensionOnItsOwnReferenceString_WhenScanned_ThenNotMistakenForNestedReference
        // and gap #2 in docs/features/sdk-migration/ignixa-upstream-gaps.md for the full mechanism.
        Assert.Equal(3, handles.Count);
        Assert.Contains("Practitioner/prac1", values);
        Assert.Contains("Organization/org2", values);
        Assert.Contains(values, v => v == null);

        Assert.DoesNotContain("http://example.org/expr/ext99", values);
        Assert.DoesNotContain("http://example.org/expr/cond42", values);
    }

    [Fact]
    public void GivenReferenceWithPrimitiveExtensionOnItsOwnReferenceString_WhenScanned_ThenNotMistakenForNestedReference()
    {
        // Regression test for a confirmed Ignixa schema-derivation quirk in SchemaAwareElement.Children()
        // (Ignixa.Serialization, SDK 0.6.7): a case-insensitive "recursive BackboneElement" heuristic --
        // meant for genuine self-nesting types like QuestionnaireResponse.item.item -- compares a
        // child's field name against the parent type's name using StringComparison.OrdinalIgnoreCase.
        // For a Reference-typed parent, the child field literally named "reference" case-insensitively
        // matches the type name "Reference", so the child is wrongly stamped with InstanceType ==
        // "Reference" (the parent's own type) instead of "string". This is NOT caused by
        // ISchema.GetTypeDefinition's case-insensitive type registry -- that is a separate, real, but
        // non-causal latent defect on this path (Children() never performs a bare-name
        // GetTypeDefinition("reference") lookup here); see gap #2 in
        // docs/features/sdk-migration/ignixa-upstream-gaps.md for both.
        //
        // Because the scanner recurses into Reference nodes (for Reference.identifier.assigner), it
        // would otherwise also visit and misidentify this leaf. The scanner's `!HasPrimitiveValue`
        // guard must filter it out: a real Reference is always object-valued, never primitive-valued.
        var root = ParseToElement(PlanDefinitionAdversarialJson, _r4Schema);

        // Direct probe: pins the underlying SDK behavior itself, so this test actually fails when the
        // upstream SchemaAwareElement.Children() recursion heuristic is fixed (at which point remove
        // this pin, the guard becomes defensive-only, and the gap row/this comment should be updated).
        // This assertion is independent of any handle/Reference-value assertion style -- unlike
        // `h.Reference == "..."`, it can't silently stop detecting the phantom just because the
        // phantom's Reference property happens to read as null.
        IElement subject = root.Children("subject").Single();
        IElement phantomReferenceStringElement = subject.Children("reference").Single();
        Assert.Equal("Reference", phantomReferenceStringElement.InstanceType);

        // Behavioral assertion: proves the guard itself matters to the scanner's output (mutation-
        // verified -- fails with the guard removed, passes with it restored). A
        // `Assert.Single(handles, h => h.Reference == "Practitioner/prac1")`-style assertion does NOT
        // work here: the phantom's `.Reference` reads null whether or not it is yielded, so only a
        // count-based assertion actually depends on the guard's presence.
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();
        Assert.Equal(3, handles.Count);
    }

    [Fact]
    public void GivenExpressionReferenceInActionCondition_WhenNavigatedDirectly_ThenSchemaTypeIsUriNotReference()
    {
        // Directly proves the disambiguation mechanism the scanner relies on: the schema classifies
        // Expression.reference as "uri", not "Reference", even though the JSON property name matches.
        var root = ParseToElement(PlanDefinitionAdversarialJson, _r4Schema);

        IElement expression = root.Children("action").Single()
            .Children("condition").Single()
            .Children("expression").Single();
        Assert.Equal("Expression", expression.InstanceType);

        IElement expressionReference = expression.Children("reference").Single();
        Assert.Equal("uri", expressionReference.InstanceType);
    }

    [Fact]
    public void GivenChoiceTypedSubjectReference_WhenNavigatedDirectly_ThenSchemaTypeIsReference()
    {
        var root = ParseToElement(PlanDefinitionAdversarialJson, _r4Schema);

        IElement subject = root.Children("subject").Single();
        Assert.Equal("Reference", subject.InstanceType);
    }

    [Fact]
    public void GivenReferenceWithShadowExtension_WhenSetReference_ThenShadowPropertyIsPreserved()
    {
        var resourceNode = _serializer.Parse(PlanDefinitionAdversarialJson);
        var root = resourceNode.ToElement(_r4Schema);

        IgnixaReferenceHandle handle = IgnixaReferenceScanner.EnumerateReferences(root)
            .Single(h => h.Reference == "Practitioner/prac1");

        handle.SetReference("Practitioner/prac999");

        Assert.Equal("Practitioner/prac999", handle.Reference);

        var subjectReferenceObject = (JsonObject)((IMutableJsonNode)resourceNode).MutableNode["subjectReference"]!;
        Assert.True(subjectReferenceObject.TryGetPropertyValue("_reference", out JsonNode? shadowNode));
        Assert.NotNull(shadowNode);
        Assert.Equal(
            "shadow-preserved",
            shadowNode!["extension"]![0]!["valueString"]!.GetValue<string>());
    }

    [Fact]
    public void GivenImmunizationEducationReference_WhenScanned_ThenNotYielded()
    {
        const string immunizationJson = """
            {
                "resourceType": "Immunization",
                "id": "imm1",
                "status": "completed",
                "vaccineCode": { "text": "Test vaccine" },
                "patient": { "reference": "Patient/pat1" },
                "occurrenceDateTime": "2024-01-01",
                "education": [
                    {
                        "reference": "http://example.org/edu/1"
                    }
                ]
            }
            """;

        var root = ParseToElement(immunizationJson, _r4Schema);
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        var singleReference = Assert.Single(handles);
        Assert.Equal("Patient/pat1", singleReference.Reference);
    }

    [Fact]
    public void GivenExtensionValueReference_WhenScanned_ThenYielded()
    {
        const string json = """
            {
                "resourceType": "Basic",
                "id": "basic1",
                "code": { "text": "test" },
                "extension": [
                    {
                        "url": "http://example.org/fhir/StructureDefinition/ref-ext",
                        "valueReference": { "reference": "Organization/org3" }
                    }
                ]
            }
            """;

        var root = ParseToElement(json, _r4Schema);
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        var singleReference = Assert.Single(handles);
        Assert.Equal("Organization/org3", singleReference.Reference);
    }

    [Fact]
    public void GivenContainedResourceWithReference_WhenScanned_ThenYielded()
    {
        const string json = """
            {
                "resourceType": "Patient",
                "id": "pat-with-contained",
                "contained": [
                    {
                        "resourceType": "Observation",
                        "id": "obs1",
                        "status": "final",
                        "code": { "text": "test" },
                        "subject": { "reference": "Patient/pat2" }
                    }
                ]
            }
            """;

        var root = ParseToElement(json, _r4Schema);
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        var singleReference = Assert.Single(handles);
        Assert.Equal("Patient/pat2", singleReference.Reference);
    }

    [Fact]
    public void GivenActorDefinitionReference_WhenScannedWithR5Schema_ThenNotYielded()
    {
        const string json = """
            {
                "resourceType": "ActorDefinition",
                "id": "act1",
                "status": "draft",
                "type": "person",
                "reference": ["http://example.org/actor-doc"]
            }
            """;

        var root = ParseToElement(json, _r5Schema);
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        Assert.Empty(handles);
    }

    [Fact]
    public void GivenRequirementsReference_WhenScannedWithR5Schema_ThenNotYielded()
    {
        const string json = """
            {
                "resourceType": "Requirements",
                "id": "req1",
                "status": "draft",
                "reference": ["http://example.org/req-doc"]
            }
            """;

        var root = ParseToElement(json, _r5Schema);
        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        Assert.Empty(handles);
    }

    [Fact]
    public void GivenNullRoot_WhenEnumerateReferences_ThenThrows()
    {
        Assert.Throws<ArgumentNullException>(() => IgnixaReferenceScanner.EnumerateReferences(null!).ToList());
    }

    private IElement ParseToElement(string json, ISchema schema)
    {
        var resourceNode = _serializer.Parse(json);
        return resourceNode.ToElement(schema);
    }
}
