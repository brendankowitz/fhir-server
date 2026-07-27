// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Ignixa.Search.Sql.Builders;

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

        /// <summary>
        /// Attempts to produce an executable Ignixa SQL plan for <paramref name="searchOptions"/>. Returns
        /// <see langword="null"/> when Ignixa execution is disabled, the request is ineligible, compilation
        /// reports a capability gap, or the compiled plan's result shape is not yet safe to materialise. In
        /// every null case the caller falls back to the legacy SQL path.
        /// </summary>
        /// <param name="searchOptions">The SQL Server search options carrying the canonical Ignixa options.</param>
        /// <param name="accessControlPredicateRequired">
        /// Whether the caller requires an access-control predicate. Requests that need access-control
        /// predicates are skipped because the Ignixa compiler does not yet model them.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An executable plan, or <see langword="null"/> to fall back to the legacy path.</returns>
        Task<IgnixaSqlExecutionPlan> TryCreateExecutionPlanAsync(
            SqlSearchOptions searchOptions,
            bool accessControlPredicateRequired,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// An executable Ignixa SQL plan: the emitted parameterized SQL plus the metadata the reader needs to
    /// materialise its rows.
    /// </summary>
    /// <param name="EmittedSql">The emitted parameterized SQL and its typed parameters.</param>
    /// <param name="HasIncludes">Whether the plan carries includes, so rows expose <c>IsMatch</c>/<c>IsPartial</c>.</param>
    /// <param name="CountOnly">Whether the plan emits a single count scalar rather than resource rows.</param>
    internal sealed record IgnixaSqlExecutionPlan(
        EmittedSql EmittedSql,
        bool HasIncludes,
        bool CountOnly);
}
