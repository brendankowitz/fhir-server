// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Text.Json.Nodes;
using EnsureThat;
using Ignixa.Abstractions;

namespace Microsoft.Health.Fhir.Ignixa;

/// <summary>
/// A handle to a single Reference-typed element's mutable JSON backing object.
/// </summary>
/// <remarks>
/// Obtained via <see cref="IElement.Meta{T}"/>'s <see cref="JsonNode"/> escape hatch, which returns
/// the live <see cref="JsonObject"/> backing the element -- mutating it through <see cref="SetReference"/>
/// mutates the resource's JSON in place.
/// </remarks>
public readonly struct IgnixaReferenceHandle : IEquatable<IgnixaReferenceHandle>
{
    private readonly JsonObject? _referenceObject;

    internal IgnixaReferenceHandle(IElement element)
    {
        _referenceObject = element.Meta<JsonNode>() as JsonObject;
    }

    /// <summary>
    /// Gets a value indicating whether this handle has a live JSON object backing it.
    /// </summary>
    internal bool HasReferenceObject => _referenceObject != null;

    /// <summary>
    /// Gets the current value of the <c>reference</c> string property, or <c>null</c> if absent
    /// (e.g. a display-only or identifier-only Reference with nothing to resolve).
    /// </summary>
    public string? Reference =>
        _referenceObject != null
        && _referenceObject.TryGetPropertyValue("reference", out JsonNode? node)
        && node is JsonValue value
            ? value.GetValue<string>()
            : null;

    public static bool operator ==(IgnixaReferenceHandle left, IgnixaReferenceHandle right) => left.Equals(right);

    public static bool operator !=(IgnixaReferenceHandle left, IgnixaReferenceHandle right) => !left.Equals(right);

    /// <summary>
    /// Sets the <c>reference</c> string property in place. Any <c>_reference</c> primitive-extension
    /// shadow property (a sibling key) is untouched -- this only replaces the "reference" entry.
    /// </summary>
    public void SetReference(string value)
    {
        EnsureArg.IsNotNull(_referenceObject, nameof(_referenceObject));
        _referenceObject["reference"] = value;
    }

    /// <inheritdoc/>
    public bool Equals(IgnixaReferenceHandle other) => ReferenceEquals(_referenceObject, other._referenceObject);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IgnixaReferenceHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _referenceObject?.GetHashCode() ?? 0;
}
