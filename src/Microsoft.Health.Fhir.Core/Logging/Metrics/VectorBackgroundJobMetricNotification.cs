// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Logging.Metrics
{
    /// <summary>
    /// Contains measurements produced by one background embedding job.
    /// </summary>
    public sealed class VectorBackgroundJobMetricNotification
    {
        /// <summary>
        /// Gets or sets the number of vector rows written.
        /// </summary>
        public int RowsWritten { get; set; }

        /// <summary>
        /// Gets or sets the number of resources skipped because their vector state was unchanged.
        /// </summary>
        public int ResourcesSkipped { get; set; }
    }
}
