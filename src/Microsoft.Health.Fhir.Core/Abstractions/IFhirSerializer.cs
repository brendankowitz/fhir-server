// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Abstractions
{
    /// <summary>
    /// Provides an abstraction over FHIR resource serialization and deserialization.
    /// This interface allows switching between different FHIR SDK implementations
    /// (e.g., Firely SDK, Ignixa SDK) without changing the consuming code.
    /// </summary>
    public interface IFhirSerializer
    {
        /// <summary>
        /// Gets the resource format supported by this serializer (JSON or XML).
        /// </summary>
        FhirResourceFormat Format { get; }

        /// <summary>
        /// Serializes a FHIR resource to a string representation.
        /// </summary>
        /// <param name="resource">The resource to serialize.</param>
        /// <returns>A string representation of the resource in the serializer's format.</returns>
        string SerializeToString(Resource resource);

        /// <summary>
        /// Deserializes a string representation of a FHIR resource.
        /// </summary>
        /// <typeparam name="T">The type of resource to deserialize.</typeparam>
        /// <param name="data">The string data to deserialize.</param>
        /// <returns>The deserialized resource.</returns>
        T Parse<T>(string data) where T : Resource;
    }
}
