// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Validation;
using Ignixa.Validation.Abstractions;
using Ignixa.Validation.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Create;
using Microsoft.Health.Fhir.Core.Features.Validation;
using Microsoft.Health.Fhir.Core.Features.Validation.Narratives;
using Microsoft.Health.Fhir.Core.Messages.Create;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Validation;

[Trait(Traits.OwningTeam, OwningTeam.Fhir)]
[Trait(Traits.Category, Categories.Validate)]
public class IgnixaResourceValidatorTests
{
    private readonly IgnixaJsonSerializer _serializer = new IgnixaJsonSerializer();
    private readonly IIgnixaSchemaContext _schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
    private readonly IgnixaResourceValidator _validator;

    public IgnixaResourceValidatorTests()
    {
        // skipFallbackOnSuccess: false preserves the pre-US-11 behavior these existing tests assert
        // (Firely fallback always runs after a successful Ignixa validation).
        _validator = new IgnixaResourceValidator(_schemaContext, new ModelAttributeValidator(), skipFallbackOnSuccess: false);
    }

    [Fact]
    public async Task GivenIgnixaResourceWithInvalidDateTime_WhenValidating_ThenInvalidShouldBeReturned()
    {
        // Arrange
        var resource = await CreateResourceElement(ObservationWithInvalidDateTimeJson);
        var results = new List<ValidationResult>();

        // Act
        var isValid = _validator.TryValidate(resource, results, recurse: false);

        // Assert
        Assert.False(isValid);
        Assert.Contains(results, x => x.ErrorMessage.Contains("format"));
    }

    [Fact]
    public async Task GivenIgnixaResourceWithValidDateTimeWithoutOffset_WhenValidating_ThenValidShouldBeReturned()
    {
        // Arrange
        var resource = await CreateResourceElement(ObservationWithValidDateTimeWithoutOffsetJson);
        var results = new List<ValidationResult>();

        // Act
        var isValid = _validator.TryValidate(resource, results, recurse: false);

        // Assert
        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GivenIgnixaResourceWithInvalidDateTime_WhenValidatingCreate_ThenInvalidShouldBeReturned()
    {
        // Arrange
        var resource = await CreateResourceElement(ObservationWithInvalidDateTimeJson);
        var validator = CreateCreateResourceValidator();

        // Act
        var result = validator.Validate(new CreateResourceRequest(resource, bundleResourceContext: null));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("format"));
    }

    [Fact]
    public async Task GivenValidIgnixaResource_WhenSkipFallbackOnSuccess_ThenFirelyFallbackIsNotInvoked()
    {
        // Arrange
        var fallbackSpy = new CountingFallbackValidator();
        var validator = new IgnixaResourceValidator(_schemaContext, fallbackSpy, skipFallbackOnSuccess: true);
        var resource = await CreateResourceElement(ObservationWithValidDateTimeWithoutOffsetJson);
        var results = new List<ValidationResult>();

        // Act
        var isValid = validator.TryValidate(resource, results, recurse: false);

        // Assert
        Assert.True(isValid);
        Assert.Empty(results);
        Assert.Equal(0, fallbackSpy.CallCount);
    }

    [Fact]
    public async Task GivenValidIgnixaResource_WhenNotSkippingFallback_ThenFirelyFallbackIsInvokedExactlyOnce()
    {
        // Arrange - Hybrid-equivalent behavior: fallback still runs as a safety net.
        var fallbackSpy = new CountingFallbackValidator();
        var validator = new IgnixaResourceValidator(_schemaContext, fallbackSpy, skipFallbackOnSuccess: false);
        var resource = await CreateResourceElement(ObservationWithValidDateTimeWithoutOffsetJson);
        var results = new List<ValidationResult>();

        // Act
        var isValid = validator.TryValidate(resource, results, recurse: false);

        // Assert
        Assert.True(isValid);
        Assert.Equal(1, fallbackSpy.CallCount);
    }

    // -------------------------------------------------------------------------------------------------
    // Half B evidence: conformance checks for CodeSystem/ValueSet are registered in Ignixa's profile
    // (Full) tier and do NOT execute at the Compatibility depth IgnixaResourceValidator runs at. These
    // tests validate an intentionally-invalid instance through the exact schema-build/validate path the
    // validator uses, at both Compatibility and Full depth, and assert the conformance issue only
    // surfaces at Full. That is the evidence for keeping CodeSystem and ValueSet on the exclusion list:
    // removing them would let these invalid instances pass, since the validator never runs Full depth.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GivenInvalidCodeSystem_WhenValidatingViaIgnixaSchema_ThenConformanceIssueOnlySurfacesAtFullDepth()
    {
        // Arrange - concept.property "effectiveDate" is declared dateTime but carries a valueBoolean,
        // which CodeSystemPropertyTypeCheck (csp-1) is designed to reject.
        var node = await ParseResourceNode(InvalidCodeSystemJson);
        var schema = BuildSchema("CodeSystem");
        var element = node.ToElement(_schemaContext.Schema);

        // Act
        var compatibility = schema.Validate(element, CompatibilitySettings(), new ValidationState().WithInstance("CodeSystem", node.Id));
        var full = schema.Validate(element, FullSettings(), new ValidationState().WithInstance("CodeSystem", node.Id));

        // Assert - Ignixa catches it, but only at Full depth (which the validator does not use).
        Assert.Contains(full.Issues, i => i.Message.Contains("invalid type"));
        Assert.DoesNotContain(compatibility.Issues, i => i.Message.Contains("invalid type"));
    }

    [Fact]
    public async Task GivenInvalidValueSet_WhenValidatingViaIgnixaSchema_ThenConformanceIssueOnlySurfacesAtFullDepth()
    {
        // Arrange - compose.include.system is a fragment (#local), not an absolute URI, which
        // ValueSetIncludeSystemCheck (vs-1) is designed to reject.
        var node = await ParseResourceNode(InvalidValueSetJson);
        var schema = BuildSchema("ValueSet");
        var element = node.ToElement(_schemaContext.Schema);

        // Act
        var compatibility = schema.Validate(element, CompatibilitySettings(), new ValidationState().WithInstance("ValueSet", node.Id));
        var full = schema.Validate(element, FullSettings(), new ValidationState().WithInstance("ValueSet", node.Id));

        // Assert - Ignixa catches it, but only at Full depth (which the validator does not use).
        Assert.Contains(full.Issues, i => i.Message.Contains("must be absolute"));
        Assert.DoesNotContain(compatibility.Issues, i => i.Message.Contains("must be absolute"));
    }

    private const string InvalidCodeSystemJson = """
        {
          "resourceType": "CodeSystem",
          "status": "active",
          "content": "complete",
          "property": [
            {
              "code": "effectiveDate",
              "type": "dateTime"
            }
          ],
          "concept": [
            {
              "code": "abc",
              "property": [
                {
                  "code": "effectiveDate",
                  "valueBoolean": true
                }
              ]
            }
          ]
        }
        """;

    private const string InvalidValueSetJson = """
        {
          "resourceType": "ValueSet",
          "status": "active",
          "compose": {
            "include": [
              {
                "system": "#localCodeSystem"
              }
            ]
          }
        }
        """;

    private const string ObservationWithInvalidDateTimeJson = """
        {
          "resourceType": "Observation",
          "status": "final",
          "code": {
            "coding": [
              {
                "system": "http://loinc.org",
                "code": "29463-7",
                "display": "Body Weight"
              }
            ]
          },
          "effectiveDateTime": "2021-10-13+02:00",
          "valueQuantity": {
            "value": 185,
            "unit": "lbs",
            "system": "http://unitsofmeasure.org",
            "code": "[lb_av]"
          }
        }
        """;

    private const string ObservationWithValidDateTimeWithoutOffsetJson = """
        {
          "resourceType": "Observation",
          "status": "final",
          "code": {
            "coding": [
              {
                "system": "http://loinc.org",
                "code": "29463-7",
                "display": "Body Weight"
              }
            ]
          },
          "effectiveDateTime": "1980-05-11T16:32:15",
          "valueQuantity": {
            "value": 185,
            "unit": "lbs",
            "system": "http://unitsofmeasure.org",
            "code": "[lb_av]"
          }
        }
        """;

    private CreateResourceValidator CreateCreateResourceValidator()
    {
        var contextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
        contextAccessor.RequestContext.RequestHeaders.Returns(new Dictionary<string, StringValues>());

        return new CreateResourceValidator(
            _validator,
            new NarrativeHtmlSanitizer(NullLogger<NarrativeHtmlSanitizer>.Instance, Options.Create(new CoreFeatureConfiguration())),
            Substitute.For<IProfileValidator>(),
            contextAccessor,
            Options.Create(new CoreFeatureConfiguration()));
    }

    private async Task<ResourceElement> CreateResourceElement(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ResourceJsonNode resourceNode = await _serializer.ParseAsync(stream);
        var ignixaElement = new IgnixaResourceElement(resourceNode, _schemaContext.Schema);

        return ignixaElement.ToResourceElement();
    }

    private async Task<ResourceJsonNode> ParseResourceNode(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await _serializer.ParseAsync(stream);
    }

    private ValidationSchema BuildSchema(string resourceType)
    {
        var typeDefinition = _schemaContext.Schema.GetTypeDefinition(resourceType);
        Assert.NotNull(typeDefinition);

        return new StructureDefinitionSchemaBuilder().BuildSchema(typeDefinition, _schemaContext.Schema, terminologyService: null);
    }

    // Mirrors the depth/terminology settings IgnixaResourceValidator uses in production.
    private static ValidationSettings CompatibilitySettings() => new ValidationSettings
    {
        Depth = ValidationDepth.Compatibility,
        SkipTerminologyValidation = true,
    };

    private static ValidationSettings FullSettings() => new ValidationSettings
    {
        Depth = ValidationDepth.Full,
        SkipTerminologyValidation = true,
    };

    private sealed class CountingFallbackValidator : ModelAttributeValidator
    {
        public int CallCount { get; private set; }

        public override bool TryValidate(ResourceElement value, ICollection<ValidationResult> validationResults = null, bool recurse = false)
        {
            CallCount++;
            return base.TryValidate(value, validationResults, recurse);
        }
    }
}
