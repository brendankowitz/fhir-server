// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Models
{
    /// <summary>
    /// Specifies how text values are extracted for vector search indexing.
    /// </summary>
    public enum VectorTextExtractionPolicy
    {
        /// <summary>
        /// Index only the first extracted text value.
        /// </summary>
        FirstValue,

        /// <summary>
        /// Concatenate all extracted text values into one indexed value.
        /// </summary>
        Concatenate,

        /// <summary>
        /// Index each extracted text value in a separate row.
        /// </summary>
        PerValueRow,
    }

    /// <summary>
    /// Configures vector search indexing for a search parameter.
    /// </summary>
    public class VectorSearchParameterConfig
    {
        /// <summary>
        /// The canonical URL of the vector search configuration extension.
        /// </summary>
        public const string ExtensionUrl = "http://microsoft.com/fhir/StructureDefinition/vector-search-config";

        /// <summary>
        /// Gets or sets how text values are extracted before vector indexing.
        /// </summary>
        public VectorTextExtractionPolicy ExtractionPolicy { get; set; } = VectorTextExtractionPolicy.Concatenate;

        /// <summary>
        /// Gets or sets the maximum number of tokens submitted for vector indexing.
        /// </summary>
        public int MaxInputTokens { get; set; } = 8000;
    }
}
