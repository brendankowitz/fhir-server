// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Reads the row shape produced by Ignixa-emitted search SQL. Ignixa projects <c>(T1, Sid1)</c> — or
    /// <c>(T1, Sid1, IsMatch, IsPartial)</c> when the plan carries includes — followed by the
    /// <see cref="ProjectionColumns"/> projected from <c>dbo.Resource</c>. This is a different column
    /// layout from the legacy generator, so it is materialised here rather than through
    /// <c>SqlServerSearchService.ReadWrapper</c>, which continues to serve the legacy shape unchanged.
    /// </summary>
    internal static class IgnixaResourceReader
    {
        private static readonly BitColumn IsMatchColumn = new BitColumn("IsMatch");
        private static readonly BitColumn IsPartialColumn = new BitColumn("IsPartial");

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
        /// <param name="resourceTypeId">The resource type id, from <c>T1</c>.</param>
        /// <param name="resourceId">The resource id.</param>
        /// <param name="version">The resource version.</param>
        /// <param name="isDeleted">Whether the resource is deleted.</param>
        /// <param name="resourceSurrogateId">The resource surrogate id, from <c>Sid1</c>.</param>
        /// <param name="requestMethod">The originating request method.</param>
        /// <param name="isMatch">Whether the row is a match (as opposed to an included resource).</param>
        /// <param name="isPartialEntry">Whether the row is a partial include entry.</param>
        /// <param name="isRawResourceMetaSet">Whether the raw resource meta is set.</param>
        /// <param name="searchParameterHash">The search parameter hash.</param>
        /// <param name="rawResourceBytes">The compressed raw resource bytes.</param>
        /// <param name="isInvisible">Whether the row is an invisibility sentinel that should be skipped.</param>
        /// <param name="isHistory">Whether the row is a history version. Always <see langword="false"/> for the Ignixa path, which only serves latest-version searches.</param>
        public static void Read(
            SqlDataReader reader,
            bool hasIncludes,
            out short resourceTypeId,
            out string resourceId,
            out int version,
            out bool isDeleted,
            out long resourceSurrogateId,
            out string requestMethod,
            out bool isMatch,
            out bool isPartialEntry,
            out bool isRawResourceMetaSet,
            out string searchParameterHash,
            out byte[] rawResourceBytes,
            out bool isInvisible,
            out bool isHistory)
        {
            EnsureArg.IsNotNull(reader, nameof(reader));

            resourceTypeId = reader.Read(VLatest.Resource.ResourceTypeId, 0);
            resourceSurrogateId = reader.Read(VLatest.Resource.ResourceSurrogateId, 1);

            int projectionBase;
            if (hasIncludes)
            {
                isMatch = reader.Read(IsMatchColumn, 2);
                isPartialEntry = reader.Read(IsPartialColumn, 3);
                projectionBase = 4;
            }
            else
            {
                isMatch = true;
                isPartialEntry = false;
                projectionBase = 2;
            }

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
    }
}
