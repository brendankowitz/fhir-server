// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Represents the search options.
    /// </summary>
    public class SearchOptions
    {
        private int _maxItemCount;
        private int _includeCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchOptions"/> class.
        /// It hides constructor and prevent object creation not through <see cref="ISearchOptionsFactory"/>
        /// </summary>
        internal SearchOptions()
        {
        }

        internal SearchOptions(SearchOptions other)
        {
            ContinuationToken = other.ContinuationToken;
            CountOnly = other.CountOnly;
            IncludeTotal = other.IncludeTotal;
            IgnixaOptions = CloneIgnixaOptions(other.IgnixaOptions);
            IgnixaAccessControlTranslated = other.IgnixaAccessControlTranslated;
            IgnixaUnsupportedParamsAgreeWithLegacy = other.IgnixaUnsupportedParamsAgreeWithLegacy;
            OnlyIds = other.OnlyIds;
            FeedRange = other.FeedRange;
            IgnoreSearchParamHash = other.IgnoreSearchParamHash;
            IncludeContinuationTokenSearch = other.IncludeContinuationTokenSearch;

            MaxItemCountSpecifiedByClient = other.MaxItemCountSpecifiedByClient;
            Expression = other.Expression;
            SearchParameters = other.SearchParameters == null ? null : new List<SearchParameterInfo>(other.SearchParameters);
            UnsupportedSearchParams = other.UnsupportedSearchParams == null ? null : new List<Tuple<string, string>>(other.UnsupportedSearchParams);
            Sort = other.Sort == null ? null : new List<(SearchParameterInfo, SortOrder)>(other.Sort);

            if (other.MaxItemCount > 0)
            {
                MaxItemCount = other.MaxItemCount;
            }

            if (other.IncludeCount > 0)
            {
                IncludeCount = other.IncludeCount;
            }

            QueryHints = other.QueryHints == null ? null : new List<(string Param, string Value)>(other.QueryHints);

            ResourceVersionTypes = other.ResourceVersionTypes;
            IncludesContinuationToken = other.IncludesContinuationToken;
            IncludesOperationSupported = other.IncludesOperationSupported;
            IsAsyncOperation = other.IsAsyncOperation;
            SkipAppendIntersectionWithPredecessor = other.SkipAppendIntersectionWithPredecessor;
            ContainsIterativeInclude = other.ContainsIterativeInclude;
        }

        /// <summary>
        /// Gets the optional continuation token.
        /// </summary>
        public string ContinuationToken { get; internal set; }

        /// <summary>
        /// Gets the optional feed range used by CosmosDb queries.
        /// </summary>
        public string FeedRange { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether to only return the record count
        /// </summary>
        public bool CountOnly { get; internal set; }

        /// <summary>
        /// This is used for sql force reindex where we need to ignore the Resource SearchParamHash field when searching
        /// </summary>
        public bool IgnoreSearchParamHash { get; set; }

        /// <summary>
        /// Indicates if the total number of resources that match the search parameters should be calculated.
        /// </summary>
        /// <remarks>The ability to retrieve an estimate of the total is yet to be implemented.</remarks>
        public TotalType IncludeTotal { get; internal set; }

        /// <summary>
        /// Gets the maximum number of items to find.
        /// </summary>
        public int MaxItemCount
        {
            get => _maxItemCount;

            internal set
            {
                if (value <= 0)
                {
                    throw new InvalidOperationException(Core.Resources.InvalidSearchCountSpecified);
                }

                _maxItemCount = value;
            }
        }

        /// <summary>
        /// Indicates whether MaxItemCount was explicitly set by the client.
        /// </summary>
        public bool MaxItemCountSpecifiedByClient { get; internal set; }

        /// <summary>
        /// Get the number of items to include in search results.
        /// </summary>
        public int IncludeCount
        {
            get => _includeCount;
            internal set
            {
                if (value <= 0 && !IncludeContinuationTokenSearch)
                {
                    throw new InvalidOperationException(Core.Resources.InvalidSearchCountSpecified);
                }

                _includeCount = value;
            }
        }

        /// <summary>
        /// Indicates if the search is being performed just to retrieve the continuation token for includes.
        /// </summary>
        public bool IncludeContinuationTokenSearch { get; set; } = false;

        /// <summary>
        /// Which version types (latest, soft-deleted, history) to include in search.
        /// </summary>
        public ResourceVersionType ResourceVersionTypes { get; internal set; } = ResourceVersionType.Latest;

        internal bool AddCurrentClause => ResourceVersionTypes.HasFlag(ResourceVersionType.Latest) && !ResourceVersionTypes.HasFlag(ResourceVersionType.History);

        /// <summary>
        /// Gets the search expression.
        /// </summary>
        public Expression Expression { get; internal set; }

        internal Ignixa.Search.Models.SearchOptions IgnixaOptions { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this request's access control was translated into
        /// <see cref="IgnixaOptions"/> completely enough for the Ignixa compiler to enforce it on its own.
        /// </summary>
        /// <remarks>
        /// This is a security gate, so it defaults to <see langword="false"/> and is set only by
        /// <c>SearchOptionsFactory.TranslateClinicalScopesForIgnixa</c> once it has proved the translation faithful.
        /// The Ignixa router refuses any request that carries an access control predicate without this flag, which
        /// keeps a partially-translated control on the legacy path rather than letting Ignixa enforce a weaker
        /// version of it. A new access control mechanism therefore fails closed by default: it will not set this
        /// flag, so it cannot silently reach Ignixa unenforced.
        /// </remarks>
        internal bool IgnixaAccessControlTranslated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Ignixa parser and the legacy parser dropped exactly the
        /// same set of query parameters.
        /// </summary>
        /// <remarks>
        /// A search may legitimately carry parameters neither engine can honour; both simply ignore them and
        /// report them back on the bundle. What matters for routing is that the two engines ignored the *same*
        /// ones. A parameter Ignixa dropped but legacy applied would make the Ignixa result a superset of the
        /// correct rows; the reverse would make it a subset. Either way the request stays on legacy.
        /// Defaults to <see langword="false"/> so a request that never went through
        /// <c>SearchOptionsFactory</c> is treated as unverified rather than agreeing.
        /// </remarks>
        internal bool IgnixaUnsupportedParamsAgreeWithLegacy { get; set; }

        /// <summary>
        /// Gets the collection of search parameters used for filtering and querying resources.
        /// </summary>
        public IReadOnlyList<SearchParameterInfo> SearchParameters { get; internal set; } = new List<SearchParameterInfo>();

        /// <summary>
        /// Gets the list of search parameters that were not used in the search.
        /// </summary>
        public IReadOnlyList<Tuple<string, string>> UnsupportedSearchParams { get; internal set; }

        /// <summary>
        /// Gets the list of sorting parameters.
        /// </summary>
        public IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> Sort { get; internal set; }

        public IReadOnlyList<(string Param, string Value)> QueryHints { get; set; }

        public bool OnlyIds { get; set; }

        /// <summary>
        /// Flag for async operations.
        /// </summary>
        public bool IsAsyncOperation { get; internal set; }

        /// <summary>
        /// Flag for $includes operation.
        /// </summary>
        public bool IsIncludesOperation => !string.IsNullOrEmpty(IncludesContinuationToken);

        /// <summary>
        /// Gets the optional continuation token for $includes operation.
        /// </summary>
        public string IncludesContinuationToken { get; internal set; }

        /// <summary>
        /// Gets the value indicating whether or not $includes operation is supported.
        /// </summary>
        public bool IncludesOperationSupported { get; internal set; }

        /// <summary>
        /// Gets the value indicating whether or not to force Intersection With Predecessor clause in Table.Kind = normal
        /// Specifically used for smart request with ANDed query parameters multiary operation inside the union of all allowed scopes
        /// </summary>
        public bool SkipAppendIntersectionWithPredecessor { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the search contains iterative includes.
        /// </summary>
        public bool ContainsIterativeInclude { get; set; }

        /// <summary>
        /// Creates a clone of this instance.
        /// </summary>
        public SearchOptions Clone() => new SearchOptions(this);

        private static Ignixa.Search.Models.SearchOptions CloneIgnixaOptions(Ignixa.Search.Models.SearchOptions options)
        {
            if (options == null)
            {
                return null;
            }

            return new Ignixa.Search.Models.SearchOptions
            {
                MaxItemCount = options.MaxItemCount,
                ContinuationToken = options.ContinuationToken,
                Expression = options.Expression,
                Sort = options.Sort?.ToList(),
                Include = options.Include?.ToList(),
                RevInclude = options.RevInclude?.ToList(),
                Elements = options.Elements?.ToHashSet(),
                Total = options.Total,
                Summary = options.Summary,
                UnsupportedParams = options.UnsupportedParams?.ToList(),
                BundleIssues = options.BundleIssues?.ToList(),
                ResourceType = options.ResourceType,
                ResourceTypes = options.ResourceTypes?.ToList(),
                StartSurrogateId = options.StartSurrogateId,
                EndSurrogateId = options.EndSurrogateId,
                IncludesMaxItemCount = options.IncludesMaxItemCount,
                IncludesContinuationToken = options.IncludesContinuationToken,
                ResourceVersionTypes = options.ResourceVersionTypes,

                // Authorization state. A clone that drops these silently widens the request: the router clones
                // SqlSearchOptions on every row-returning search (to bump MaxItemCount for page detection), so a
                // dropped allow-list or constraint set would mean the compiled plan enforces nothing while the
                // caller believes it does. Fail-open, and invisible - the clone still "works". Any property added
                // to Ignixa.Search.Models.SearchOptions must be copied here.
                AccessConstraints = options.AccessConstraints?.ToList(),
                AllowedResourceTypes = options.AllowedResourceTypes?.ToList(),
            };
        }
    }
}
