// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Search.Expressions;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Builders;
using Ignixa.Search.Sql.Lowering;
using Ignixa.Search.Sql.Symbols;
using Microsoft.Extensions.Logging;
using Microsoft.Health.SqlServer.Features.Schema;
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Compile-only adapter that invokes the coordinated Ignixa 0.6.32 / 0.6.32-alpha SQL compiler stages
    /// (<see cref="Resolve"/>, <see cref="Lower"/>, <see cref="SqlBuilder"/>) against a <see cref="SqlSearchOptions"/>
    /// request and returns an in-memory compilation artifact.
    /// </summary>
    /// <remarks>
    /// This adapter never executes SQL, opens a connection, binds parameters, hydrates resources, or
    /// replaces the legacy FHIR Server SQL response path. It is intentionally a narrow, compile-only
    /// boundary; capability routing is added separately.
    /// </remarks>
    internal sealed class IgnixaSqlCompilerAdapter : IIgnixaSqlCompilerAdapter
    {
        private const string SearchPackageVersionValue = "0.6.32";
        private const string SearchSqlPackageVersionValue = "0.6.32-alpha";
        private const string IgnixaCommitValue = "0566dcb3e436a05afcdbcd581df702c79280693f";

        private static readonly IReadOnlyList<IncludeExpression> EmptyIncludes = Array.Empty<IncludeExpression>();
        private static readonly IReadOnlyList<SortExpression> EmptySort = Array.Empty<SortExpression>();
        private static readonly IReadOnlyList<IgnixaSearchParameterInfo> EmptyUnresolvedParameters = Array.Empty<IgnixaSearchParameterInfo>();

        private readonly ISymbolResolver _resolver;
        private readonly SchemaInformation _schemaInformation;
        private readonly ILogger<IgnixaSqlCompilerAdapter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSqlCompilerAdapter"/> class.
        /// </summary>
        /// <param name="resolver">The Ignixa SQL symbol resolver backed by the FHIR Server SQL catalog.</param>
        /// <param name="schemaInformation">The current FHIR Server SQL schema information.</param>
        /// <param name="logger">The logger. Only capability metadata is ever logged, never raw search values or SQL.</param>
        public IgnixaSqlCompilerAdapter(
            ISymbolResolver resolver,
            SchemaInformation schemaInformation,
            ILogger<IgnixaSqlCompilerAdapter> logger)
        {
            EnsureArg.IsNotNull(resolver, nameof(resolver));
            EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _resolver = resolver;
            _schemaInformation = schemaInformation;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IgnixaSqlCompilationOutcome> CompileAsync(
            SqlSearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchOptions, nameof(searchOptions));
            EnsureArg.IsNotNull(searchOptions.IgnixaOptions, nameof(searchOptions.IgnixaOptions));

            IgnixaSearchOptions ignixaOptions = searchOptions.IgnixaOptions;

            IReadOnlyList<SortExpression> requestedSort = ignixaOptions.Sort ?? EmptySort;

            // Count-only requests never carry includes/revincludes into either Resolve or Lower.
            IReadOnlyList<IncludeExpression> includes = searchOptions.CountOnly ? EmptyIncludes : (ignixaOptions.Include ?? EmptyIncludes);
            IReadOnlyList<IncludeExpression> revIncludes = searchOptions.CountOnly ? EmptyIncludes : (ignixaOptions.RevInclude ?? EmptyIncludes);

            // The legacy SQL search service issues a second query phase for sorts on parameters that may be
            // missing (e.g. descending sort with nulls last): the first phase finds resources with the sort
            // value, the second phase finds resources without it. Map that legacy flag to the corresponding
            // Ignixa sort phase; ordinary (single-phase) sorting always lowers as SortPhase.Valued.
            SortPhase sortPhase = searchOptions.SortQuerySecondPhase ? SortPhase.MissingPrimary : SortPhase.Valued;

            // Stage 1: Resolve. This is the only stage that performs I/O (symbol lookups).
            ResolvedSymbols resolved = await Resolve.RunAsync(
                ignixaOptions.Expression,
                includes,
                revIncludes,
                requestedSort,
                _resolver,
                ignixaOptions.ResourceType,
                cancellationToken);

            if (resolved.Unresolved.Count > 0)
            {
                // Known capability gap: some search parameter or resource type could not be resolved.
                // Do not lower or emit; the caller falls back to the legacy SQL path.
                return CapabilityFailure("resolve", "unresolved-symbol", resolved.Unresolved);
            }

            LoweredPlan lowered;
            try
            {
                // Stage 2: Lower. Pure, synchronous construction of the query plan.
                lowered = Lower.Run(
                    ignixaOptions.Expression,
                    resolved.Symbols,
                    ignixaOptions.ResourceType,
                    includes,
                    revIncludes,
                    ignixaOptions.IncludesMaxItemCount ?? searchOptions.IncludeCount,
                    requestedSort,
                    sortPhase,
                    page: null,
                    countOnly: searchOptions.CountOnly,
                    top: searchOptions.MaxItemCount);
            }
            catch (NotSupportedException ex)
            {
                // The developmental package surfaces capability gaps in lowering as this narrow exception
                // type. This is the only catch boundary in the adapter; cancellation, resolver/model
                // exceptions, and SqlBuilder.Run exceptions are never caught here.
                _logger.LogInformation(
                    "Ignixa lowering reported a capability gap of type {ExceptionType}.",
                    ex.GetType().Name);
                return CapabilityFailure("lower", "not-supported", EmptyUnresolvedParameters, ex.GetType().Name);
            }

            IgnixaSqlCompilationOutcome shapeFailure = ValidateResultShape(searchOptions, ignixaOptions, lowered);
            if (shapeFailure != null)
            {
                return shapeFailure;
            }

            // Stage 3: Emit. Pure, synchronous rendering of parameterized SQL. No catch boundary: any
            // exception here is unexpected and must propagate.
            EmittedSql emitted = SqlBuilder.Run(lowered.Plan);

            return new IgnixaSqlCompilationOutcome(
                Compiled: true,
                FailureStage: null,
                FailureKind: null,
                FailureMessage: null,
                LoweredPlan: lowered,
                EmittedSql: emitted,
                UnresolvedParameters: resolved.Unresolved,
                SearchPackageVersion: SearchPackageVersionValue,
                SearchSqlPackageVersion: SearchSqlPackageVersionValue,
                IgnixaCommit: IgnixaCommitValue,
                SchemaVersion: _schemaInformation.Current,
                PlanFingerprint: CreatePlanFingerprint(lowered.Plan));
        }

        /// <summary>
        /// Validates that the lowered plan's result shape matches what was requested, without parsing SQL
        /// text. Returns <see langword="null"/> when the shape matches, or a capability outcome describing
        /// the specific mismatch.
        /// </summary>
        /// <remarks>
        /// Internal (rather than private) solely so that unit tests can exercise each classification branch
        /// directly against a deliberately mismatched, hand-built <see cref="LoweredPlan"/> without depending
        /// on an undocumented divergence in the real Ignixa lowering behavior.
        /// </remarks>
        internal IgnixaSqlCompilationOutcome ValidateResultShape(
            SqlSearchOptions searchOptions,
            IgnixaSearchOptions ignixaOptions,
            LoweredPlan lowered)
        {
            QueryPlan plan = lowered.Plan;

            if (searchOptions.CountOnly != plan.CountOnly)
            {
                return CapabilityFailure("shape", "count-only-mismatch", EmptyUnresolvedParameters);
            }

            int requestedIncludeCount = searchOptions.CountOnly
                ? 0
                : (ignixaOptions.Include?.Count ?? 0) + (ignixaOptions.RevInclude?.Count ?? 0);
            int emittedIncludeCount = plan.Includes?.Count ?? 0;
            if (requestedIncludeCount != emittedIncludeCount)
            {
                return CapabilityFailure("shape", "include-shape-mismatch", EmptyUnresolvedParameters);
            }

            if ((ignixaOptions.Sort?.Count ?? 0) > 0 && plan.Sort is null)
            {
                return CapabilityFailure("shape", "sort-shape-mismatch", EmptyUnresolvedParameters);
            }

            if (searchOptions.MaxItemCount != plan.Top)
            {
                return CapabilityFailure("shape", "top-shape-mismatch", EmptyUnresolvedParameters);
            }

            return null;
        }

        /// <summary>
        /// Builds a capability failure outcome. Capability outcomes never contain raw exception text, raw
        /// search values, or a plan fingerprint; the fingerprint is only computed for successful compilations.
        /// </summary>
        private IgnixaSqlCompilationOutcome CapabilityFailure(
            string stage,
            string kind,
            IReadOnlyList<IgnixaSearchParameterInfo> unresolvedParameters,
            string failureMessage = null)
        {
            return new IgnixaSqlCompilationOutcome(
                Compiled: false,
                FailureStage: stage,
                FailureKind: kind,
                FailureMessage: failureMessage,
                LoweredPlan: null,
                EmittedSql: null,
                UnresolvedParameters: unresolvedParameters,
                SearchPackageVersion: SearchPackageVersionValue,
                SearchSqlPackageVersion: SearchSqlPackageVersionValue,
                IgnixaCommit: IgnixaCommitValue,
                SchemaVersion: _schemaInformation.Current,
                PlanFingerprint: string.Empty);
        }

        /// <summary>
        /// Creates a deterministic, redacted SHA-256 fingerprint from the plan shape only
        /// (<see cref="QueryPlan.Explain"/>). Never hashes raw search values, emitted SQL text, emitted
        /// parameter values, <see cref="Core.Features.Search.SearchOptions"/>, or exception messages.
        /// </summary>
        private static string CreatePlanFingerprint(QueryPlan plan)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plan.Explain());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
