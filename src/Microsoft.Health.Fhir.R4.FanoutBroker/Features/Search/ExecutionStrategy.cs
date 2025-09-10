// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search;

/// <summary>
/// Represents the execution strategy for fanout queries.
/// </summary>
public enum ExecutionStrategy
{
    /// <summary>
    /// Execute queries across all servers in parallel.
    /// </summary>
    Parallel,

    /// <summary>
    /// Execute queries sequentially until sufficient results are obtained.
    /// </summary>
    Sequential,
}
