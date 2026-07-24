// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ignixa.Search.Sql.Builders;
using Ignixa.Search.Sql.Lowering;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Compiles a <see cref="SqlSearchOptions"/> request into an in-memory Ignixa SQL compilation artifact.
    /// </summary>
    /// <remarks>
    /// This adapter is compile-only: it resolves symbols, lowers the canonical Ignixa expression into a
    /// query plan, and emits parameterized SQL text, but it never executes SQL, opens a connection, binds
    /// parameters, hydrates resources, or replaces the legacy FHIR Server SQL response path.
    /// </remarks>
    internal interface IIgnixaSqlCompilerAdapter
    {
        /// <summary>
        /// Compiles the canonical Ignixa expression carried by <paramref name="searchOptions"/> into a
        /// lowered plan and emitted SQL artifact, without executing it.
        /// </summary>
        /// <param name="searchOptions">The SQL Server search options, including the canonical Ignixa options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The compilation outcome, either a compiled artifact or a capability failure.</returns>
        Task<IgnixaSqlCompilationOutcome> CompileAsync(
            SqlSearchOptions searchOptions,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// The immutable result of a compile-only Ignixa SQL compilation attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="Compiled"/> value of <c>false</c> represents a known capability gap (for example an
    /// unresolved symbol, an unsupported lowering shape, or a result-shape mismatch) classified by
    /// <see cref="FailureStage"/> and <see cref="FailureKind"/>. It is never used for cancellation, resolver
    /// I/O failures, or unexpected emitter failures &#8212; those propagate as exceptions.
    /// </para>
    /// <para>
    /// <see cref="FailureMessage"/> carries capability metadata only (for example an exception type name),
    /// never raw user search values or raw exception text. <see cref="PlanFingerprint"/> is a redacted,
    /// deterministic hash of the plan shape and is empty for capability failures.
    /// </para>
    /// </remarks>
    /// <param name="Compiled">Whether compilation produced a lowered plan and emitted SQL.</param>
    /// <param name="FailureStage">The stage that reported a capability gap (<c>resolve</c>, <c>lower</c>, or <c>shape</c>), or <see langword="null"/> on success.</param>
    /// <param name="FailureKind">The specific capability failure classification, or <see langword="null"/> on success.</param>
    /// <param name="FailureMessage">Capability metadata describing the failure (never a raw exception message or search value), or <see langword="null"/> on success.</param>
    /// <param name="LoweredPlan">The lowered Ignixa query plan, or <see langword="null"/> when compilation did not succeed.</param>
    /// <param name="EmittedSql">The emitted parameterized SQL and its typed parameters, or <see langword="null"/> when compilation did not succeed.</param>
    /// <param name="UnresolvedParameters">The search parameters that the symbol resolver could not resolve.</param>
    /// <param name="SearchPackageVersion">The coordinated Ignixa.Search package version.</param>
    /// <param name="SearchSqlPackageVersion">The coordinated Ignixa.Search.Sql package version.</param>
    /// <param name="IgnixaCommit">The Ignixa commit identity the coordinated packages were built from.</param>
    /// <param name="SchemaVersion">The current FHIR Server SQL schema version, when known.</param>
    /// <param name="PlanFingerprint">A redacted, deterministic SHA-256 fingerprint of the plan shape, or an empty string for capability failures.</param>
    internal sealed record IgnixaSqlCompilationOutcome(
        bool Compiled,
        string FailureStage,
        string FailureKind,
        string FailureMessage,
        LoweredPlan LoweredPlan,
        EmittedSql EmittedSql,
        IReadOnlyList<IgnixaSearchParameterInfo> UnresolvedParameters,
        string SearchPackageVersion,
        string SearchSqlPackageVersion,
        string IgnixaCommit,
        int? SchemaVersion,
        string PlanFingerprint);
}
