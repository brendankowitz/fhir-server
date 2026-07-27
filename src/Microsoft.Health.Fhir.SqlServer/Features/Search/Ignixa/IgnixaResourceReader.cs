// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Reads the row shape produced by Ignixa-emitted search SQL. Ignixa projects <c>(T1, Sid1)</c> — or
    /// <c>(T1, Sid1, IsMatch, IsPartial)</c> when the plan carries includes — then one <c>SortValueN</c>
    /// keyset column per active sort key when the plan carries a custom sort, followed by the
    /// <see cref="ProjectionColumns"/> projected from <c>dbo.Resource</c>. This is a different column
    /// layout from the legacy generator, so it is materialised here rather than through
    /// <c>SqlServerSearchService.ReadWrapper</c>, which continues to serve the legacy shape unchanged.
    /// </summary>
    internal static class IgnixaResourceReader
    {
        /// <summary>
        /// The ordered list of <c>dbo.Resource</c> columns Ignixa must project so that a row carries every
        /// field <c>ReadWrapper</c> produces except <c>ResourceTypeId</c> (supplied by <c>T1</c>) and
        /// <c>ResourceSurrogateId</c> (supplied by <c>Sid1</c>). The order here is authoritative: the emitter
        /// renders the columns in this sequence and <see cref="Read"/> reads them at the matching ordinals.
        /// </summary>
        public static readonly IReadOnlyList<string> ProjectionColumns = new[]
        {
            "ResourceId",
            "Version",
            "IsDeleted",
            "RequestMethod",
            "IsRawResourceMetaSet",
            "SearchParamHash",
            "RawResource",
        };

        /// <summary>
        /// Reads a single Ignixa result row, producing the same out-parameters as
        /// <c>SqlServerSearchService.ReadWrapper</c>. Columns are read in strictly ascending ordinal order so
        /// the reader remains compatible with <see cref="System.Data.CommandBehavior.SequentialAccess"/>.
        /// </summary>
        /// <param name="reader">The open data reader positioned on a row.</param>
        /// <param name="hasIncludes">
        /// Whether the compiled plan carries includes. When <see langword="true"/>, the row supplies
        /// <c>IsMatch</c> and <c>IsPartial</c> at ordinals 2 and 3; when <see langword="false"/> those columns
        /// are absent and every row is treated as a non-partial match.
        /// </param>
        /// <param name="sortKeyColumnCount">
        /// The number of <c>SortValueN</c> keyset columns Ignixa projects between the identity/flag prefix and
        /// the resource projection. Zero when the plan carries no custom sort. The reader skips these columns
        /// to reach the projection at the correct ordinal.
        /// </param>
        /// <param name="captureSortValue">
        /// Whether to read the primary key's <c>SortValue0</c> column into <paramref name="primarySortValue"/>
        /// so the caller can mint a continuation token. Only meaningful when
        /// <paramref name="sortKeyColumnCount"/> is greater than zero.
        /// </param>
        /// <param name="resourceTypeId">The resource type id, from <c>T1</c>.</param>
        /// <param name="resourceId">The resource id.</param>
        /// <param name="version">The resource version.</param>
        /// <param name="isDeleted">Whether the resource is deleted.</param>
        /// <param name="resourceSurrogateId">The resource surrogate id, from <c>Sid1</c>.</param>
        /// <param name="requestMethod">The originating request method.</param>
        /// <param name="isMatch">Whether the row is a match (as opposed to an included resource).</param>
        /// <param name="isPartialEntry">Whether the row is a partial include entry.</param>
        /// <param name="primarySortValue">
        /// The raw value of the primary sort key's <c>SortValue0</c> column, or <see langword="null"/> when it
        /// was not captured. The caller formats it into the continuation token.
        /// </param>
        /// <param name="isRawResourceMetaSet">Whether the raw resource meta is set.</param>
        /// <param name="searchParameterHash">The search parameter hash.</param>
        /// <param name="rawResourceBytes">The compressed raw resource bytes.</param>
        /// <param name="isInvisible">Whether the row is an invisibility sentinel that should be skipped.</param>
        /// <param name="isHistory">Whether the row is a history version. Always <see langword="false"/> for the Ignixa path, which only serves latest-version searches.</param>
        public static void Read(
            SqlDataReader reader,
            bool hasIncludes,
            int sortKeyColumnCount,
            bool captureSortValue,
            out short resourceTypeId,
            out string resourceId,
            out int version,
            out bool isDeleted,
            out long resourceSurrogateId,
            out string requestMethod,
            out bool isMatch,
            out bool isPartialEntry,
            out object primarySortValue,
            out bool isRawResourceMetaSet,
            out string searchParameterHash,
            out byte[] rawResourceBytes,
            out bool isInvisible,
            out bool isHistory)
        {
            EnsureArg.IsNotNull(reader, nameof(reader));

            resourceTypeId = reader.Read(VLatest.Resource.ResourceTypeId, 0);
            resourceSurrogateId = reader.Read(VLatest.Resource.ResourceSurrogateId, 1);

            int prefixColumns;
            if (hasIncludes)
            {
                // Ignixa unions the match arm (IsPartial = CAST(0 AS bit)) with each include arm, whose
                // IsPartial is CASE WHEN COUNT_BIG(*) OVER() > Limit THEN 1 ELSE 0 END — an int. SQL Server's
                // UNION ALL data-type precedence promotes the combined IsPartial column to int, so it must be
                // read as a 0/1 flag rather than a bit. IsMatch stays a bit in every arm, but both are read the
                // same tolerant way so that a flag's SQL static type is not silently load-bearing here.
                isMatch = ReadBooleanFlag(reader, 2);
                isPartialEntry = ReadBooleanFlag(reader, 3);
                prefixColumns = 4;
            }
            else
            {
                isMatch = true;
                isPartialEntry = false;
                prefixColumns = 2;
            }

            // Ignixa projects the SortValueN keyset columns immediately after the identity/flag prefix and
            // before the resource projection. Read the primary key's value (SortValue0) when the continuation
            // token needs it, then advance past any remaining keyset columns; sequential access forbids
            // revisiting an earlier column, not skipping a later one.
            if (captureSortValue && sortKeyColumnCount > 0)
            {
                object rawSortValue = reader.GetValue(prefixColumns);
                primarySortValue = rawSortValue is DBNull ? null : rawSortValue;
            }
            else
            {
                primarySortValue = null;
            }

            int projectionBase = prefixColumns + sortKeyColumnCount;
            resourceId = reader.Read(VLatest.Resource.ResourceId, projectionBase);
            version = reader.Read(VLatest.Resource.Version, projectionBase + 1);
            isDeleted = reader.Read(VLatest.Resource.IsDeleted, projectionBase + 2);
            requestMethod = reader.Read(VLatest.Resource.RequestMethod, projectionBase + 3);
            isRawResourceMetaSet = reader.Read(VLatest.Resource.IsRawResourceMetaSet, projectionBase + 4);
            searchParameterHash = reader.Read(VLatest.Resource.SearchParamHash, projectionBase + 5);
            rawResourceBytes = reader.GetSqlBytes(projectionBase + 6).Value;
            isInvisible = rawResourceBytes.Length == 1 && rawResourceBytes[0] == 0xF;
            isHistory = false;
        }

        /// <summary>
        /// Reads a 0/1 boolean flag column tolerant of its emitted SQL type.
        /// </summary>
        /// <remarks>
        /// This originally worked around an Ignixa defect: the match arm emitted <c>CAST(0 AS bit) AS IsPartial</c>
        /// while an include stage emitted a bare int <c>CASE</c>, and T-SQL union type precedence promoted the
        /// column to <c>int</c>, so <see cref="SqlDataReader.GetBoolean(int)"/> threw on include rows only. That
        /// defect is fixed upstream (both arms now emit <c>bit</c>) and pinned by a regression test there, so this
        /// coercion is no longer load-bearing. It is kept deliberately: the column's type is decided by a
        /// generator in another repository, and tolerating either representation costs one boxed read per flag
        /// while turning a future type drift into correct results rather than a runtime cast failure on a code
        /// path that only executes for searches carrying includes.
        /// </remarks>
        private static bool ReadBooleanFlag(SqlDataReader reader, int ordinal)
        {
            object value = reader.GetValue(ordinal);
            return value is not DBNull && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
    }
}
