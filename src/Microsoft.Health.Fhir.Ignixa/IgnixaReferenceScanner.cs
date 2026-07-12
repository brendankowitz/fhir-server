// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Ignixa.Abstractions;

namespace Microsoft.Health.Fhir.Ignixa;

/// <summary>
/// Walks a schema-typed Ignixa <see cref="IElement"/> tree and yields every element whose
/// schema-declared type is <c>Reference</c>.
/// </summary>
/// <remarks>
/// Disambiguates by <see cref="IElement.InstanceType"/> (the type the schema assigned to the
/// element), not by JSON property name. Several FHIR elements are named "reference" but are NOT
/// Reference datatypes -- e.g. <c>Expression.reference</c> and <c>Immunization.education.reference</c>
/// are both type <c>uri</c>. A sparse Reference instance (e.g. display-only, no "reference" property)
/// is structurally identical to these under a schema-free JSON walk, so only a schema-typed tree can
/// tell them apart.
/// </remarks>
/// <remarks>
/// <para>
/// Also guards against a confirmed Ignixa schema-derivation quirk: the <c>Reference</c> complex
/// type's own <c>reference</c> field (a plain <c>string</c>) is itself schema-typed as
/// <c>InstanceType == "Reference"</c>. The cause is <c>SchemaAwareElement.Children()</c>'s
/// case-insensitive "recursive BackboneElement" heuristic (in Ignixa.Serialization) -- meant for
/// genuine self-nesting types like <c>QuestionnaireResponse.item.item</c> -- which compares a
/// child's field name against the parent type's name using
/// <c>StringComparison.OrdinalIgnoreCase</c>. For a <c>Reference</c>-typed parent, the child field
/// literally named "reference" matches the type name "Reference" case-insensitively, so the child
/// is wrongly stamped with the parent's own type. This is a distinct latent SDK defect from
/// <c>ISchema.GetTypeDefinition</c>'s case-insensitive type registry (also real, but not the cause
/// here -- <c>Children()</c> never performs a bare-name lookup for this child; see gap #2 in
/// docs/features/sdk-migration/ignixa-upstream-gaps.md for both). Since this scanner deliberately
/// recurses into Reference nodes (to find e.g. <c>Reference.identifier.assigner</c>), every
/// Reference's own "reference" leaf would otherwise be visited and misidentified as a nested
/// Reference too.
/// </para>
/// <para>
/// A genuine Reference is always a JSON object (<see cref="IElement.HasPrimitiveValue"/> is
/// <c>false</c>); the common phantom shape (mistyped "reference" leaf holding a plain string value)
/// is JSON-string-valued (<see cref="IElement.HasPrimitiveValue"/> is <c>true</c>). Requiring
/// <c>!HasPrimitiveValue</c> excludes that shape using another schema-derived structural fact -- not
/// the property name -- keeping the type-not-name disambiguation principle intact.
/// </para>
/// <para>
/// Residual case: for extension-only Reference input (a <c>reference</c> string absent but a
/// primitive extension present via the <c>_reference</c> shadow property, e.g.
/// <c>"subjectReference": {"_reference": {"extension": [...]}}</c> -- valid FHIR), the phantom
/// child has no primitive value and passes this guard too, so it is still yielded, with
/// <see cref="IgnixaReferenceHandle.Reference"/> reading as <c>null</c>. This is currently harmless
/// because no consumer calls <see cref="IgnixaReferenceHandle.SetReference"/> without first checking
/// <c>Reference != null</c>; a future unconditional caller of <c>SetReference</c> would corrupt the
/// <c>_reference</c> shadow object, so this should be revisited if that pattern appears.
/// </para>
/// </remarks>
public static class IgnixaReferenceScanner
{
    /// <summary>
    /// Enumerates every Reference-typed element reachable from <paramref name="root"/>, including
    /// references nested inside other references (e.g. <c>Reference.identifier.assigner</c>).
    /// </summary>
    /// <param name="root">The schema-typed element to scan (typically a resource root).</param>
    /// <returns>A handle per Reference-typed element found, in document order.</returns>
    public static IEnumerable<IgnixaReferenceHandle> EnumerateReferences(IElement root)
    {
        EnsureArg.IsNotNull(root, nameof(root));

        return EnumerateReferenceElements(root)
            .Select(element => new IgnixaReferenceHandle(element))
            .Where(handle => handle.HasReferenceObject);
    }

    private static IEnumerable<IElement> EnumerateReferenceElements(IElement element)
    {
        foreach (IElement child in element.Children())
        {
            if (child.InstanceType == "Reference" && !child.HasPrimitiveValue)
            {
                yield return child;
            }

            // Recurse INTO Reference nodes too -- Reference.identifier.assigner is itself a Reference.
            foreach (IElement nested in EnumerateReferenceElements(child))
            {
                yield return nested;
            }
        }
    }
}
