// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Logging.Metrics
{
    /// <summary>
    /// Contains measurements for one vector search query.
    /// </summary>
    public sealed class VectorSearchQueryMetricNotification
    {
        /// <summary>
        /// Gets or sets a value indicating whether the approximate query shape was generated.
        /// </summary>
        public bool IsApproximate { get; set; }
    }
}
