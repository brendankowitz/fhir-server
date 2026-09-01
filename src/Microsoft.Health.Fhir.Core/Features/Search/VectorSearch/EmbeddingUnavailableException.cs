// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Search.VectorSearch
{
    /// <summary>
    /// Represents an unavailable embedding service.
    /// </summary>
    public class EmbeddingUnavailableException : FhirException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EmbeddingUnavailableException"/> class.
        /// </summary>
        public EmbeddingUnavailableException()
        {
            Issues.Add(new OperationOutcomeIssue(
                    OperationOutcomeConstants.IssueSeverity.Error,
                    OperationOutcomeConstants.IssueType.Transient,
                    global::Microsoft.Health.Fhir.Core.Resources.EmbeddingUnavailable));
        }
    }
}
