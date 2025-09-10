// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search;

/// <summary>
/// Represents an include or revinclude parameter.
/// </summary>
internal class IncludeParameter
{
    /// <summary>
    /// Gets or sets the type of include parameter.
    /// </summary>
    public IncludeType Type { get; set; }

    /// <summary>
    /// Gets or sets the value of the include parameter.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
