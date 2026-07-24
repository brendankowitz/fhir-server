// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Observes eligible search requests by invoking the Ignixa SQL compiler and logging the compilation
    /// outcome — without executing the emitted SQL, binding parameters, opening a connection, or
    /// replacing the legacy FHIR Server SQL response path.
    /// </summary>
    internal interface IIgnixaSqlCompileOnlyRouter
    {
        /// <summary>
        /// Compiles the Ignixa SQL for <paramref name="searchOptions"/> when the request is eligible, and
        /// logs the outcome. Never executes SQL or replaces the search response.
        /// </summary>
        /// <param name="searchOptions">The SQL Server search options carrying the canonical Ignixa options.</param>
        /// <param name="accessControlPredicateRequired">
        /// Whether the caller requires an access-control predicate. Requests that need access-control
        /// predicates are skipped because the Ignixa compiler does not yet model them.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task ObserveAsync(
            SqlSearchOptions searchOptions,
            bool accessControlPredicateRequired,
            CancellationToken cancellationToken);
    }
}
