// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Text.Json.Nodes;
using Ignixa.Abstractions;
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
        // Regression test for a confirmed Ignixa schema-derivation quirk: Reference's own "reference"
        // field (a plain string) is itself schema-typed InstanceType=="Reference" -- apparently a
        // case-insensitive collision in the schema's type registry between the field name "reference"
        // and the datatype name "Reference" (schema.GetTypeDefinition("reference") returns the
        // Reference type). Because the scanner recurses into Reference nodes (for
        // Reference.identifier.assigner), it would otherwise also visit and misidentify this leaf.
        // The scanner's `!HasPrimitiveValue` guard must filter it out: a real Reference is always
        // object-valued, never primitive-valued.
        var root = ParseToElement(PlanDefinitionAdversarialJson, _r4Schema);

        var handles = IgnixaReferenceScanner.EnumerateReferences(root).ToList();

        // Exactly one handle should carry "Practitioner/prac1" -- the genuine subjectReference --
        // not two (which would happen if the mistyped "reference" leaf were also yielded).
        Assert.Single(handles, h => h.Reference == "Practitioner/prac1");
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

        var subjectReferenceObject = (JsonObject)resourceNode.MutableNode["subjectReference"]!;
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
