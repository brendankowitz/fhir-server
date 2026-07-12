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
/// <c>InstanceType == "Reference"</c> -- apparently a case-insensitive collision inside the schema's
/// type registry between the field name "reference" and the datatype name "Reference"
/// (<c>ISchema.GetTypeDefinition("reference")</c> returns the <c>Reference</c> type definition).
/// Since this scanner deliberately recurses into Reference nodes (to find e.g.
/// <c>Reference.identifier.assigner</c>), every Reference's own "reference" leaf would otherwise be
/// visited and misidentified as a nested Reference too.
/// </para>
/// <para>
/// A genuine Reference is always a JSON object (<see cref="IElement.HasPrimitiveValue"/> is
/// <c>false</c>); the mistyped "reference" leaf is always a JSON string primitive
/// (<see cref="IElement.HasPrimitiveValue"/> is <c>true</c>). Requiring
/// <c>!HasPrimitiveValue</c> excludes the false match using another schema-derived structural fact
/// -- not the property name -- keeping the type-not-name disambiguation principle intact.
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
