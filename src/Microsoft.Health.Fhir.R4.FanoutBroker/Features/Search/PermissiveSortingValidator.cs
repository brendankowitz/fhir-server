// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// A permissive sorting validator used by the Fanout Broker. Since the broker
    /// forwards search requests to downstream FHIR servers (which enforce their own
    /// sorting constraints), this implementation always allows the requested sort
    /// specification. Any true validation should occur at the individual server.
    /// </summary>
    public class PermissiveSortingValidator : ISortingValidator
    {
        public bool ValidateSorting(IReadOnlyList<(SearchParameterInfo searchParameter, SortOrder sortOrder)> sorting, out IReadOnlyList<string> errorMessages)
        {
            errorMessages = System.Array.Empty<string>();
            return true;
        }
    }
}
