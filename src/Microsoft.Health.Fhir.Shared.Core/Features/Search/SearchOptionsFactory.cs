// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search.Access;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Models;
using Expression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;
using IgnixaSortExpression = Ignixa.Search.Expressions.SortExpression;
using IgnixaSortOrder = Ignixa.Search.Expressions.SortOrder;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    public class SearchOptionsFactory : ISearchOptionsFactory
    {
        /// <summary>
        /// The allow-list entry that denies everything. Named rather than left as a literal because its whole
        /// meaning is that no resource type can carry this name, so it resolves to the compiler's
        /// unmatchable-type sentinel - the counterpart to legacy's <c>ResourceType = "none"</c> blocking
        /// predicate. An empty allow-list cannot serve here: the compiler reads empty as unconstrained.
        /// </summary>
        private const string DeniedResourceTypeSentinel = "none";

        /// <summary>
        /// The Device reference parameter the SMART compartment device restriction keys on. Must match
        /// <c>SmartCompartmentSearchRewriter</c>'s constant of the same name.
        /// </summary>
        private const string DevicePatientSearchParameterCode = "patient";

        /// <summary>
        /// The Ignixa <c>_id</c> and <c>_type</c> parameters, used to hand-build the resource-column legs of a
        /// SMART compartment union. The compiler dispatches these two by <see cref="Ignixa.Search.Models.SearchParameterInfo.Code"/>
        /// onto dbo.Resource's own columns and never looks them up in the symbol table, so only the code has to
        /// be right; the URL is carried for parity with the bound form.
        /// </summary>
        private static readonly Ignixa.Search.Models.SearchParameterInfo IgnixaIdSearchParameter =
            new("_id", "_id", Ignixa.Specification.ValueSets.Normative.SearchParamType.Token, new Uri("http://hl7.org/fhir/SearchParameter/Resource-id"));

        private static readonly Ignixa.Search.Models.SearchParameterInfo IgnixaTypeSearchParameter =
            new("_type", "_type", Ignixa.Specification.ValueSets.Normative.SearchParamType.Token, new Uri("http://hl7.org/fhir/SearchParameter/Resource-type"));

        private static readonly string SupportedTotalTypes = $"'{TotalType.Accurate}', '{TotalType.None}'".ToLower(CultureInfo.CurrentCulture);

        private readonly IExpressionParser _expressionParser;
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor;
        private readonly ISortingValidator _sortingValidator;
        private readonly ExpressionAccessControl _expressionAccess;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ILogger _logger;
        private readonly CoreFeatureConfiguration _featureConfiguration;
        private readonly IIgnixaSearchOptionsAdapter _ignixaSearchOptionsAdapter;
        private readonly IgnixaSearchTenantAccessor _ignixaSearchTenantAccessor;
        private SearchParameterInfo _resourceTypeSearchParameter;
        private readonly HashSet<string> _queryHintParameterNames = new() { KnownQueryParameterNames.GlobalEndSurrogateId, KnownQueryParameterNames.EndSurrogateId, KnownQueryParameterNames.StartSurrogateId, KnownQueryParameterNames.IgnoreSearchParamHash };

        public SearchOptionsFactory(
            IExpressionParser expressionParser,
            ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver searchParameterDefinitionManagerResolver,
            IOptions<CoreFeatureConfiguration> featureConfiguration,
            RequestContextAccessor<IFhirRequestContext> contextAccessor,
            ISortingValidator sortingValidator,
            ExpressionAccessControl expressionAccess,
            IIgnixaSearchOptionsAdapter ignixaSearchOptionsAdapter,
            IgnixaSearchTenantAccessor ignixaSearchTenantAccessor,
            ILogger<SearchOptionsFactory> logger)
        {
            EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
            EnsureArg.IsNotNull(searchParameterDefinitionManagerResolver, nameof(searchParameterDefinitionManagerResolver));
            EnsureArg.IsNotNull(featureConfiguration?.Value, nameof(featureConfiguration));
            EnsureArg.IsNotNull(contextAccessor, nameof(contextAccessor));
            EnsureArg.IsNotNull(sortingValidator, nameof(sortingValidator));
            EnsureArg.IsNotNull(expressionAccess, nameof(expressionAccess));
            EnsureArg.IsNotNull(ignixaSearchOptionsAdapter, nameof(ignixaSearchOptionsAdapter));
            EnsureArg.IsNotNull(ignixaSearchTenantAccessor, nameof(ignixaSearchTenantAccessor));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _expressionParser = expressionParser;
            _contextAccessor = contextAccessor;
            _sortingValidator = sortingValidator;
            _expressionAccess = expressionAccess;
            _searchParameterDefinitionManager = searchParameterDefinitionManagerResolver();
            _logger = logger;
            _featureConfiguration = featureConfiguration.Value;
            _ignixaSearchOptionsAdapter = ignixaSearchOptionsAdapter;
            _ignixaSearchTenantAccessor = ignixaSearchTenantAccessor;
        }

        private SearchParameterInfo ResourceTypeSearchParameter
        {
            get
            {
                if (_resourceTypeSearchParameter == null)
                {
#if Stu3 || R4 || R4B
                    _resourceTypeSearchParameter = _searchParameterDefinitionManager.GetSearchParameter(ResourceType.Resource.ToString(), SearchParameterNames.ResourceType);
#else
                    _resourceTypeSearchParameter = _searchParameterDefinitionManager.GetSearchParameter(KnownResourceTypes.Resource, SearchParameterNames.ResourceType);
#endif
                }

                return _resourceTypeSearchParameter;
            }
        }

        public SearchOptions Create(string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters, bool isAsyncOperation = false, ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest, bool onlyIds = false, bool isIncludesOperation = false)
        {
            return Create(null, null, resourceType, queryParameters, isAsyncOperation, resourceVersionTypes: resourceVersionTypes, onlyIds: onlyIds, isIncludesOperation: isIncludesOperation);
        }

        public SearchOptions Create(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation = false,
            bool useSmartCompartmentDefinition = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            var searchOptions = new SearchOptions();

            if (queryParameters != null && queryParameters.Any(_ => _.Item1 == KnownQueryParameterNames.GlobalEndSurrogateId && _.Item2 != null))
            {
                var queryHint = new List<(string param, string value)>();

                foreach (var par in queryParameters.Where(x => x.Item1 == KnownQueryParameterNames.Type || _queryHintParameterNames.Contains(x.Item1)))
                {
                    queryHint.Add((par.Item1, par.Item2));
                }

                searchOptions.QueryHints = queryHint;
            }

            searchOptions.IgnoreSearchParamHash = queryParameters != null && queryParameters.Any(_ => _.Item1 == KnownQueryParameterNames.IgnoreSearchParamHash && _.Item2 != null);

            string continuationToken = null;
            string feedRange = null;

            // $includes related parameters
            string includesContinuationToken = null;
            int? includesCount = null;

            var searchParams = new SearchParams();
            var unsupportedSearchParameters = new List<Tuple<string, string>>();
            var ignixaQueryParameters = new List<Tuple<string, string>>();
            bool setDefaultBundleTotal = true;
            var notReferencedSearches = new List<string>();

            // Extract the continuation token, filter out the other known query parameters that's not search related.
            // Exclude time travel parameters from evaluation to avoid warnings about unsupported parameters
            foreach (Tuple<string, string> query in queryParameters?.Where(_ => !_queryHintParameterNames.Contains(_.Item1)) ?? Enumerable.Empty<Tuple<string, string>>())
            {
                if (query.Item1 == KnownQueryParameterNames.ContinuationToken)
                {
                    // This is an unreachable case. The mapping of the query parameters makes it so only one continuation token can exist.
                    if (continuationToken != null)
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.MultipleQueryParametersNotAllowed, KnownQueryParameterNames.ContinuationToken));
                    }

                    continuationToken = ContinuationTokenEncoder.Decode(query.Item2);
                    setDefaultBundleTotal = false;
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.FeedRange, StringComparison.OrdinalIgnoreCase))
                {
                    feedRange = query.Item2;
                }
                else if (query.Item1 == KnownQueryParameterNames.Format || query.Item1 == KnownQueryParameterNames.Pretty)
                {
                    // _format and _pretty are not search parameters, so we can ignore them.
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Type, StringComparison.OrdinalIgnoreCase))
                {
                    ignixaQueryParameters.Add(query);

                    if (string.IsNullOrWhiteSpace(query.Item2))
                    {
                        throw new BadRequestException(string.Format(Core.Resources.InvalidTypeParameter, query.Item2));
                    }

                    var types = query.Item2.SplitByOrSeparator();
                    var badTypes = types.Where(type => !ModelInfoProvider.IsKnownResource(type)).ToHashSet();

                    if (badTypes.Count != 0)
                    {
                        _contextAccessor.RequestContext?.BundleIssues.Add(
                            new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Warning,
                                OperationOutcomeConstants.IssueType.NotSupported,
                                string.Format(Core.Resources.InvalidTypeParameter, badTypes.OrderBy(x => x).Select(type => $"'{type}'").JoinByOrSeparator())));
                        if (badTypes.Count != types.Count)
                        {
                            // In case of we have acceptable types, we filter invalid types from search.
                            searchParams.Add(KnownQueryParameterNames.Type, types.Except(badTypes).JoinByOrSeparator());
                        }
                        else
                        {
                            // If all types are invalid, we add them to search params. If we remove them, we wouldn't filter by type, and return all types,
                            // which is incorrect behaviour. Optimally we should indicate in search options what it would yield nothing, and skip search,
                            // but there is no option for that right now.
                            searchParams.Add(KnownQueryParameterNames.Type, query.Item2);
                        }
                    }
                    else
                    {
                        searchParams.Add(KnownQueryParameterNames.Type, query.Item2);
                    }
                }
                else if (string.IsNullOrWhiteSpace(query.Item1) || string.IsNullOrWhiteSpace(query.Item2))
                {
                    // Query parameter with empty value is not supported.
                    unsupportedSearchParameters.Add(query);
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Text, StringComparison.OrdinalIgnoreCase))
                {
                    // Query parameter _text is not allowed for any resource.
                    unsupportedSearchParameters.Add(query);
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Total, StringComparison.OrdinalIgnoreCase))
                {
                    ignixaQueryParameters.Add(query);

                    if (Enum.TryParse<TotalType>(query.Item2, true, out var totalType))
                    {
                        ValidateTotalType(totalType);

                        searchOptions.IncludeTotal = totalType;
                        setDefaultBundleTotal = false;
                    }
                    else
                    {
                        throw new BadRequestException(string.Format(Core.Resources.InvalidTotalParameter, query.Item2, SupportedTotalTypes));
                    }
                }
                else if (query.Item1 == KnownQueryParameterNames.Count && Convert.ToInt32(query.Item2) == 0)
                {
                    ignixaQueryParameters.Add(query);

                    try
                    {
                        searchParams.Add(KnownQueryParameterNames.Summary, SummaryType.Count.ToString());
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.NotReferenced, StringComparison.OrdinalIgnoreCase))
                {
                    notReferencedSearches.Add(query.Item2);
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.IncludesContinuationToken, StringComparison.OrdinalIgnoreCase))
                {
                    // This is an unreachable case. The mapping of the query parameters makes it so only one continuation token can exist.
                    if (includesContinuationToken != null)
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.MultipleQueryParametersNotAllowed, KnownQueryParameterNames.IncludesContinuationToken));
                    }

                    if (isIncludesOperation)
                    {
                        includesContinuationToken = ContinuationTokenEncoder.Decode(query.Item2);
                        setDefaultBundleTotal = false;
                    }
                    else
                    {
                        _contextAccessor.RequestContext?.BundleIssues.Add(
                            new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Information,
                                OperationOutcomeConstants.IssueType.Informational,
                                Core.Resources.IncludesContinuationTokenIgnored));
                    }
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.IncludesCount, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(query.Item2, out int count) && count > 0)
                    {
                        includesCount = count;
                    }
                    else
                    {
                        throw new BadRequestException(Core.Resources.InvalidSearchIncludesCountSpecified);
                    }
                }
                else
                {
                    ignixaQueryParameters.Add(query);

                    // Parse the search parameters.
                    try
                    {
                        // Basic format checking (e.g. integer value for _count key etc.).
                        searchParams.Add(query.Item1, query.Item2);
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }
            }

            if (isIncludesOperation && string.IsNullOrEmpty(includesContinuationToken))
            {
                throw new BadRequestException(Core.Resources.MissingIncludesContinuationToken);
            }

            searchOptions.ContinuationToken = continuationToken;
            searchOptions.IncludesContinuationToken = includesContinuationToken;
            searchOptions.IncludesOperationSupported = _featureConfiguration.SupportsIncludes;
            searchOptions.FeedRange = feedRange;

            if (setDefaultBundleTotal)
            {
                ValidateTotalType(_featureConfiguration.IncludeTotalInBundle);
                searchOptions.IncludeTotal = _featureConfiguration.IncludeTotalInBundle;
            }

            // Check the item count.
            if (searchParams.Count != null)
            {
                searchOptions.MaxItemCountSpecifiedByClient = true;

                if (isAsyncOperation)
                {
                    searchOptions.IsAsyncOperation = true;
                    searchOptions.MaxItemCount = searchParams.Count.Value;
                }
                else if (searchParams.Count > _featureConfiguration.MaxItemCountPerSearch)
                {
                    searchOptions.MaxItemCount = _featureConfiguration.MaxItemCountPerSearch;

                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.SearchParamaterCountExceedLimit, _featureConfiguration.MaxItemCountPerSearch, searchParams.Count)));
                }
                else
                {
                    searchOptions.MaxItemCount = searchParams.Count.Value;
                }
            }
            else
            {
                searchOptions.MaxItemCount = _featureConfiguration.DefaultItemCountPerSearch;
            }

            if (includesCount.HasValue && includesCount <= _featureConfiguration.MaxIncludeCountPerSearch)
            {
                searchOptions.IncludeCount = includesCount.Value;
            }
            else
            {
                if (includesCount.HasValue)
                {
                    searchOptions.IncludeCount = _featureConfiguration.MaxIncludeCountPerSearch;
                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.SearchParamaterIncludesCountExceedLimit, _featureConfiguration.MaxIncludeCountPerSearch, includesCount)));
                }
                else
                {
                    searchOptions.IncludeCount = _featureConfiguration.DefaultIncludeCountPerSearch;
                }
            }

            if (searchParams.Elements?.Any() == true && searchParams.Summary != null && searchParams.Summary != SummaryType.False)
            {
                // The search parameters _elements and _summarize cannot be specified for the same request.
                throw new BadRequestException(string.Format(Core.Resources.ElementsAndSummaryParametersAreIncompatible, KnownQueryParameterNames.Summary, KnownQueryParameterNames.Elements));
            }

            searchOptions.OnlyIds = onlyIds;

            // Check to see if only the count should be returned
            searchOptions.CountOnly = searchParams.Summary == SummaryType.Count;

            // If the resource type is not specified, then the common
            // search parameters should be used.
            string[] parsedResourceTypes = new[] { KnownResourceTypes.DomainResource };

            var searchExpressions = new List<Expression>();
            if (string.IsNullOrWhiteSpace(resourceType))
            {
                // Try to parse resource types from _type Search Parameter
                // This will result in empty array if _type has any modifiers
                // Which is good, since :not modifier changes the meaning of the
                // search parameter and we can no longer use it to deduce types
                // (and should proceed with ResourceType.DomainResource in that case)
                var resourceTypes = searchParams.Parameters
                    .Where(q => q.Item1 == KnownQueryParameterNames.Type) // <-- Equality comparison to avoid modifiers
                    .SelectMany(q => q.Item2.SplitByOrSeparator())
                    .Where(q => ModelInfoProvider.IsKnownResource(q))
                    .Distinct().ToList();

                if (resourceTypes.Any())
                {
                    parsedResourceTypes = resourceTypes.ToArray();
                }
            }
            else
            {
                parsedResourceTypes[0] = resourceType;
                if (!ModelInfoProvider.IsKnownResource(resourceType))
                {
                    throw new ResourceNotSupportedException(resourceType);
                }

                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, resourceType, false)));
            }

            var resourceTypesString = parsedResourceTypes.Select(x => x.ToString()).ToArray();
            AppendWildcardScopeParametersForIgnixa(ignixaQueryParameters);
            searchOptions.IgnixaOptions = _ignixaSearchOptionsAdapter.Build(resourceType, ignixaQueryParameters, _ignixaSearchTenantAccessor.TenantId);
            AddIgnixaBundleIssues(searchOptions.IgnixaOptions);

            // Form all the include revinclude expressions before for the Smart queries access control check
            // Collect all the resource types required by the include/revinclude expressions
            var includeRevincludeSearchExpressions = new List<IncludeExpression>();
            includeRevincludeSearchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams.Include, resourceTypesString, false).Where(e => e != null));
            includeRevincludeSearchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams.RevInclude, resourceTypesString, true).Where(e => e != null));
            var requiredResourceTypes = includeRevincludeSearchExpressions.SelectMany(x => x.Produces).ToList();

            // Add the parsed resource types to the required resource types for access control check
            // Now it contains all the resource types that are requested by the search,
            // including those from the search path, _type parameter, and resource types returned via include/revinclude expressions
            requiredResourceTypes.AddRange(parsedResourceTypes);

            CheckFineGrainedAccessControl(searchExpressions, searchParams, requiredResourceTypes);

            // Translate the same clinical scopes into the Ignixa allow-list. Runs immediately after the legacy
            // check so both observe identical state, and deliberately re-reads AccessControlContext rather than
            // reusing CheckFineGrainedAccessControl's locals: this must be a faithful independent translation,
            // not a by-product of legacy expression building.
            TranslateClinicalScopesForIgnixa(searchOptions);

            var validSearchParameters = new List<SearchParameterInfo>();

            // Deduplicate exact (name, value) query parameter pairs before parsing. Repeated identical parameters produce
            // redundant database lookups (one per expression) without affecting query semantics. Per FHIR spec, repeated
            // parameters are AND semantics (set intersection), and AND of identical predicates is idempotent: X AND X ≡ X.
            searchExpressions.AddRange(searchParams.Parameters.Distinct().Select(
            q =>
            {
                try
                {
                    var parsed = LegacyExpressionProjection(resourceTypesString, q.Item1, q.Item2);

                    foreach (var resourceTypeString in resourceTypesString)
                    {
                        if (_searchParameterDefinitionManager.TryGetSearchParameter(resourceTypeString, q.Item1, out var searchParameter))
                        {
                            validSearchParameters.Add(searchParameter);
                        }
                    }

                    return parsed;
                }
                catch (SearchParameterNotSupportedException)
                {
                    unsupportedSearchParameters.Add(q);

                    return null;
                }
            })
            .Where(item => item != null));

            searchOptions.SearchParameters = validSearchParameters;

            // Parse _include:iterate (_include:recurse) parameters.
            // _include:iterate (_include:recurse) expression may appear without a preceding _include parameter
            // when applied on a circular reference
            if (includeRevincludeSearchExpressions.Any())
            {
                searchExpressions.AddRange(includeRevincludeSearchExpressions);

                if (includeRevincludeSearchExpressions.Any(expression => expression.Iterate))
                {
                    searchOptions.ContainsIterativeInclude = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(compartmentType))
            {
                if (Enum.TryParse(compartmentType, out CompartmentType parsedCompartmentType))
                {
                    if (string.IsNullOrWhiteSpace(compartmentId))
                    {
                        throw new InvalidSearchOperationException(Core.Resources.CompartmentIdIsInvalid);
                    }

                    if (useSmartCompartmentDefinition)
                    {
                        searchOptions.IgnixaSmartCompartmentSearch = true;
                        searchOptions.IgnixaSmartCompartmentTranslated =
                            TryAppendIgnixaSmartCompartmentExpression(searchOptions, compartmentType, compartmentId, resourceTypesString);
                        searchExpressions.Add(Expression.SmartCompartmentSearch(compartmentType, compartmentId, resourceTypesString));
                    }
                    else
                    {
                        AppendIgnixaCompartmentExpression(searchOptions, compartmentType, compartmentId, resourceTypesString);
                        searchExpressions.Add(Expression.CompartmentSearch(compartmentType, compartmentId, resourceTypesString));
                    }
                }
                else
                {
                    throw new InvalidSearchOperationException(string.Format(Core.Resources.CompartmentTypeIsInvalid, compartmentType));
                }
            }

            if (!string.IsNullOrWhiteSpace(_contextAccessor.RequestContext?.AccessControlContext?.CompartmentResourceType))
            {
                var smartCompartmentType = _contextAccessor.RequestContext?.AccessControlContext?.CompartmentResourceType;
                var smartCompartmentId = _contextAccessor.RequestContext?.AccessControlContext?.CompartmentId;

                if (Enum.TryParse(smartCompartmentType, out CompartmentType parsedCompartmentType))
                {
                    if (string.IsNullOrWhiteSpace(smartCompartmentId))
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.FhirUserClaimIsNotAValidResource, _contextAccessor.RequestContext?.AccessControlContext.FhirUserClaim));
                    }

                    // Don't add the smart compartment twice. this is a patch for bug number AB#152447.
                    if (!searchExpressions.Any(e => e.ValueInsensitiveEquals(Expression.SmartCompartmentSearch(smartCompartmentType, smartCompartmentId, null))))
                    {
                        searchOptions.IgnixaSmartCompartmentSearch = true;
                        searchOptions.IgnixaSmartCompartmentTranslated =
                            TryAppendIgnixaSmartCompartmentExpression(searchOptions, smartCompartmentType, smartCompartmentId, resourceTypesString);
                        searchExpressions.Add(Expression.SmartCompartmentSearch(smartCompartmentType, smartCompartmentId, resourceTypesString));
                    }
                }
                else
                {
                    throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.FhirUserClaimIsNotAValidResource, _contextAccessor.RequestContext?.AccessControlContext.FhirUserClaim));
                }
            }

            var otherSearchErrors = new List<string>();
            var invalidSearchParameters = new List<Tuple<string, string>>();

            foreach (var notReferencedSearch in notReferencedSearches)
            {
                try
                {
                    var expression = _expressionParser.ParseNotReferenced(notReferencedSearch);

                    if (expression != null)
                    {
                        searchExpressions.Add(expression);
                    }
                }
                catch (FhirException e)
                {
                    otherSearchErrors.Add(e.Issues.First().Diagnostics);
                    invalidSearchParameters.Add(Tuple.Create(KnownQueryParameterNames.NotReferenced, notReferencedSearch));
                }
            }

            if (searchExpressions.Count == 1)
            {
                searchOptions.Expression = searchExpressions[0];
            }
            else if (searchExpressions.Count > 1)
            {
                searchOptions.Expression = Expression.And(searchExpressions.ToArray());
            }

            if (searchOptions.IgnixaOptions?.UnsupportedParams != null)
            {
                // Params Ignixa could not handle that legacy did handle. These are the dangerous ones for the
                // Ignixa SQL path: legacy applies a filter that Ignixa silently dropped, so routing the request
                // to Ignixa would return a superset of the correct rows. The router refuses such a request.
                // The reverse case - a param legacy dropped but Ignixa understood - is equally a divergence
                // (Ignixa would return fewer rows than legacy), so both directions clear the agreement flag.
                List<Tuple<string, string>> droppedOnlyByIgnixa = searchOptions.IgnixaOptions.UnsupportedParams
                    .Select(param => Tuple.Create(param, string.Empty))
                    .Where(param => !unsupportedSearchParameters.Any(existing => existing.Item1 == param.Item1))
                    .ToList();

                bool droppedOnlyByLegacy = unsupportedSearchParameters
                    .Any(existing => !searchOptions.IgnixaOptions.UnsupportedParams.Contains(existing.Item1, StringComparer.OrdinalIgnoreCase));

                searchOptions.IgnixaUnsupportedParamsAgreeWithLegacy = droppedOnlyByIgnixa.Count == 0 && !droppedOnlyByLegacy;

                unsupportedSearchParameters.AddRange(droppedOnlyByIgnixa);
            }
            else
            {
                // No Ignixa parse happened (or it reported nothing), so nothing is known about agreement. Any
                // param legacy dropped is therefore unverified.
                searchOptions.IgnixaUnsupportedParamsAgreeWithLegacy = unsupportedSearchParameters.Count == 0;
            }

            // A chained search that references nothing is a legacy-only diagnostic with no Ignixa counterpart,
            // so it cannot be shown to agree.
            if (invalidSearchParameters.Count > 0)
            {
                searchOptions.IgnixaUnsupportedParamsAgreeWithLegacy = false;
            }

            invalidSearchParameters.AddRange(unsupportedSearchParameters);
            searchOptions.UnsupportedSearchParams = invalidSearchParameters;

            // Sort is not needed for summary count
            if (searchParams.Sort?.Count > 0 && searchParams.Summary != SummaryType.Count)
            {
                var sortings = new List<(SearchParameterInfo, SortOrder)>(searchParams.Sort.Count);
                bool sortingsValid = true;

                // Only parameters that are valid for searching can also be used as sort parameter values. Therefore first check if the sort parameter values are valid as search parameters.
                foreach ((string, Hl7.Fhir.Rest.SortOrder) sorting in searchParams.Sort)
                {
                    try
                    {
                        SearchParameterInfo searchParameterInfo = resourceTypesString.Select(t => _searchParameterDefinitionManager.GetSearchParameter(t, sorting.Item1)).Distinct().First();
                        sortings.Add((searchParameterInfo, sorting.Item2.ToCoreSortOrder()));
                    }
                    catch (SearchParameterNotSupportedException)
                    {
                        sortingsValid = false;
                        otherSearchErrors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SortParameterValueIsNotValidSearchParameter, sorting.Item1, string.Join(", ", resourceTypesString)));
                    }
                }

                // Sort parameter values are valid search parameters. Now verify that sort parameter values are also valid for sorting.
                if (sortingsValid)
                {
                    if (!_sortingValidator.ValidateSorting(sortings, out IReadOnlyList<string> errorMessages))
                    {
                        // Sanity check, ValidateSorting must output errors if it returns false.
                        if (errorMessages == null || errorMessages.Count == 0)
                        {
                            throw new InvalidOperationException($"Expected {_sortingValidator.GetType().Name} to return error messages when {nameof(_sortingValidator.ValidateSorting)} returns false");
                        }

                        sortingsValid = false;

                        foreach (var errorMessage in errorMessages)
                        {
                            otherSearchErrors.Add(errorMessage);
                        }
                    }
                }

                if (sortingsValid)
                {
                    searchOptions.Sort = sortings;
                }
            }

            if (searchOptions.Sort == null)
            {
                searchOptions.Sort = Array.Empty<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)>();
            }

            searchOptions.IgnixaSortAgreesWithLegacy = IgnixaSortMatches(searchOptions);

            searchOptions.ResourceVersionTypes = resourceVersionTypes;

            // Processing of parameters is finished. If any of the parameters are unsupported warning is put into the bundle or exception is thrown,
            // depending on the state of the "Prefer" header.
            if (unsupportedSearchParameters.Any() || otherSearchErrors.Any())
            {
                var allErrors = new List<string>();
                foreach (Tuple<string, string> unsupported in unsupportedSearchParameters)
                {
                    allErrors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SearchParameterNotSupported, unsupported.Item1, string.Join(",", resourceTypesString)));
                }

                allErrors.AddRange(otherSearchErrors);

                var allErrorMessages = string.Empty;
                foreach (string error in allErrors)
                {
                    allErrorMessages += error + ", ";
                }

                _logger.LogDebug("Search contained errors: {Errors}", allErrorMessages);

                if (_contextAccessor.GetIsStrictHandlingEnabled())
                {
                    throw new BadRequestException(allErrors);
                }

                // There is no "Prefer" header with handling value, or handling value is valid and not set to "Prefer: handling=strict".
                foreach (string error in allErrors)
                {
                    _contextAccessor.RequestContext?.BundleIssues.Add(new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Warning,
                            OperationOutcomeConstants.IssueType.NotSupported,
                            error));
                }
            }

            _expressionAccess.CheckAndRaiseAccessExceptions(searchOptions.Expression);

            try
            {
                LogExpresssionSearchParameters(searchOptions.Expression);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Unable to log search parameters. Error: {Exception}", e.ToString());
            }

            return searchOptions;
        }

        private IEnumerable<IncludeExpression> ParseIncludeIterateExpressions(IList<(string query, IncludeModifier modifier)> includes, string[] typesString, bool isReversed)
        {
            return includes.Select(p =>
            {
                var includeResourceTypeList = typesString;
                var iterate = p.modifier == IncludeModifier.Iterate || p.modifier == IncludeModifier.Recurse;

                if (iterate)
                {
                    var includeResourceType = p.query?.Split(':')[0];
                    if (!ModelInfoProvider.IsKnownResource(includeResourceType))
                    {
                        throw new ResourceNotSupportedException(includeResourceType);
                    }

                    includeResourceTypeList = new[] { includeResourceType };
                }

                IReadOnlyCollection<string> allowedResourceTypesByScope = null;
                if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true)
                {
                    allowedResourceTypesByScope = _contextAccessor.RequestContext?.AccessControlContext?.AllowedResourceActions.Select(s => s.Resource).ToList();
                }

                var expression = _expressionParser.ParseInclude(includeResourceTypeList, p.query, isReversed, iterate, allowedResourceTypesByScope);

                // Reversed Iterate expressions (not wildcard) must specify target type if there is more than one possible target type
                if (expression.Reversed && expression.Iterate && expression.TargetResourceType == null &&
                    expression.ReferenceSearchParameter?.TargetResourceTypes?.Count > 1)
                {
                    throw new BadRequestException(
                        string.Format(Core.Resources.RevIncludeIterateTargetTypeNotSpecified, p.query));
                }

                if (expression.TargetResourceType != null &&
                   string.IsNullOrWhiteSpace(expression.TargetResourceType))
                {
                    throw new BadRequestException(
                        string.Format(Core.Resources.IncludeRevIncludeInvalidTargetResourceType, expression.TargetResourceType));
                }

                if (expression.TargetResourceType != null && !ModelInfoProvider.IsKnownResource(expression.TargetResourceType))
                {
                    throw new ResourceNotSupportedException(expression.TargetResourceType);
                }

                // For circular include iterate expressions, add an informational issue indicating that a single iteration is supported.
                // See https://www.hl7.org/fhir/search.html#revinclude.
                if (expression.Iterate && expression.CircularReference)
                {
                    var issueProperty = string.Concat(isReversed ? "_revinclude" : "_include", ":", p.modifier.ToString().ToLowerInvariant());
                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.IncludeIterateCircularReferenceExecutedOnce, issueProperty, p.query)));
                }

                if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true && !allowedResourceTypesByScope.Contains(KnownResourceTypes.All))
                {
                    if (expression.TargetResourceType != null && !allowedResourceTypesByScope.Contains(expression.TargetResourceType))
                    {
                        _logger.LogTrace("Query restricted by clinical scopes.  Target resource type {ResourceType} not included in allowed resources.", expression.TargetResourceType);
                        return null;
                    }

                    if (!expression.Produces.Any())
                    {
                        return null;
                    }
                }

                return expression;
            });
        }

        /// <summary>
        /// Translates the request's SMART clinical scopes into <see cref="Ignixa.Search.Models.SearchOptions.AllowedResourceTypes"/>
        /// and records, via <see cref="SearchOptions.IgnixaAccessControlTranslated"/>, whether that translation was
        /// complete. Only a complete translation lets the Ignixa router accept the request; anything else stays on
        /// the legacy path, which still enforces the scopes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The allow-list is the request's full scope resource list, not the scope-and-required intersection legacy
        /// ANDs into the match filter. These agree: Ignixa applies the allow-list on top of a match set already
        /// restricted to the requested types, so it computes <c>requested ∩ scopes</c>, while legacy computes
        /// <c>requested ∩ (scopes ∩ required)</c> — and <c>requested ⊆ required</c>, so the two are the same set.
        /// Using the full list additionally gives the include stages the allow-list legacy carries on
        /// <see cref="IncludeExpression.AllowedResourceTypesByScope"/>, which is the same set.
        /// </para>
        /// <para>
        /// Two shapes carry search parameters and are handled differently, because legacy handles them
        /// differently. A <em>typed</em> scope's parameters restrict which instances of that one type are
        /// visible, which is exactly Ignixa's <c>AccessConstraint</c> — a per-type predicate the compiler
        /// applies structurally to every row-producing stage, where legacy ANDs a union of
        /// <c>(_type = X AND &lt;params for X&gt;)</c> legs into the match set alone. A <c>*</c> scope's
        /// parameters are added to <c>searchParams</c> by <see cref="CheckFineGrainedAccessControl"/> and so
        /// apply to <em>every</em> type in the request; that is not a per-type constraint and has no
        /// <c>AccessConstraint</c> spelling, so it stays untranslated.
        /// </para>
        /// <para>
        /// Note that legacy only enforces a typed scope's parameters when
        /// <see cref="AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters"/> is set — with
        /// the flag off it builds the union and then discards it, keeping the type allow-list only. The
        /// translation mirrors that rather than the apparent intent, since routing must not change what a
        /// request is permitted to see.
        /// </para>
        /// <para>
        /// One case is deliberately left untranslated, because Ignixa would otherwise enforce less than legacy,
        /// and it fails closed by leaving the flag false: <b>a scope predicate Ignixa cannot parse</b>. Dropping
        /// the part it did not understand would widen what the caller may see.
        /// </para>
        /// <para>
        /// A <c>*</c> scope carrying search parameters is <em>not</em> in that group. Legacy does not treat those
        /// parameters as a per-type restriction either - it folds them into the request's own SearchParams - and
        /// <see cref="AppendWildcardScopeParametersForIgnixa"/> does the same on the Ignixa side, before the
        /// expression is built.
        /// </para>
        /// <para>
        /// The "no granted resources at all" case <em>is</em> translated, but not as an empty allow-list: an
        /// empty list means "inert" to the compiler, so that spelling would invert a total block into a total
        /// bypass. It is translated as an allow-list naming a single type that cannot exist, which the
        /// compiler resolves to its unmatchable-type sentinel — the same shape legacy's
        /// <c>ResourceType = "none"</c> predicate produces, and for the same reason.
        /// </para>
        /// </remarks>
        private void TranslateClinicalScopesForIgnixa(SearchOptions searchOptions)
        {
            IFhirRequestContext requestContext = _contextAccessor.RequestContext;
            AccessControlContext accessControl = requestContext?.AccessControlContext;

            if (searchOptions.IgnixaOptions == null || accessControl?.ApplyFineGrainedAccessControl != true)
            {
                // No fine-grained access control on this request, so there is nothing to translate and nothing
                // for the router to gate on. The flag stays false; the router only consults it when the request
                // actually carries an access control predicate.
                return;
            }

            ICollection<ScopeRestriction> restrictions = accessControl.AllowedResourceActions;
            if (restrictions == null || restrictions.Count == 0)
            {
                // Legacy blocks every query outright with a ResourceType = "none" predicate. An empty allow-list
                // would mean the opposite to the compiler, so name a type that cannot resolve: the compiler maps
                // an unresolvable name to its unmatchable-type sentinel rather than dropping it, which is the same
                // fail-closed shape by a different route.
                searchOptions.IgnixaOptions.AllowedResourceTypes = new[] { DeniedResourceTypeSentinel };
                searchOptions.IgnixaAccessControlTranslated = true;
                return;
            }

            bool enforceScopeSearchParameters = accessControl.ApplyFineGrainedAccessControlWithSearchParameters;

            if (restrictions.Any(restriction => restriction.Resource == KnownResourceTypes.All))
            {
                // A wildcard scope grants every type, which is what an absent allow-list already means. Leave it
                // empty rather than expanding to the full type list so the emitted plan stays identical to an
                // unrestricted one. Any search parameters the wildcard carries were folded into the request's own
                // Ignixa parameters by AppendWildcardScopeParametersForIgnixa before the expression was built, so
                // there is nothing left to enforce here either.
                searchOptions.IgnixaAccessControlTranslated = true;
                return;
            }

            var constraints = new List<Ignixa.Search.Models.AccessConstraint>();

            if (enforceScopeSearchParameters)
            {
                foreach (ScopeRestriction restriction in restrictions.Where(r => r.SearchParameters?.Parameters?.Any() == true))
                {
                    Ignixa.Search.Expressions.Expression predicate = BuildIgnixaScopePredicate(restriction);
                    if (predicate == null)
                    {
                        // Ignixa could not parse the whole scope predicate. Dropping the part it did not
                        // understand would widen what the caller may see, so refuse the request instead.
                        return;
                    }

                    constraints.Add(new Ignixa.Search.Models.AccessConstraint(restriction.Resource, predicate));
                }

                // AccessConstraints allows at most one entry per resource type. Two scopes on the same type are
                // two independent restrictions and both must hold, so they collapse by conjunction - the same
                // reading legacy takes when it ANDs a type's scope legs together.
                constraints = constraints
                    .GroupBy(constraint => constraint.ResourceType, StringComparer.Ordinal)
                    .Select(group => group.Count() == 1
                        ? group.First()
                        : new Ignixa.Search.Models.AccessConstraint(
                            group.Key,
                            Ignixa.Search.Expressions.Expression.And(group.Select(constraint => constraint.Predicate).ToArray())))
                    .ToList();
            }

            searchOptions.IgnixaOptions.AccessConstraints = constraints;

            searchOptions.IgnixaOptions.AllowedResourceTypes = restrictions
                .Select(restriction => restriction.Resource)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            searchOptions.IgnixaAccessControlTranslated = true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the sort Ignixa parsed is the sort the legacy path will apply,
        /// key for key and direction for direction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two lists are produced independently. <c>IgnixaOptions.Sort</c> comes from Ignixa's own binder,
        /// which sorts by anything it can order; <c>SearchOptions.Sort</c> is what survived
        /// <c>ISortingValidator</c>, which for SQL discards the <em>entire</em> sort - reporting it on the bundle
        /// instead - whenever the storage layer cannot honour it, as for a token or reference sort or any
        /// multi-key shape other than <c>(_type, _lastUpdated)</c>.
        /// </para>
        /// <para>
        /// The comparison has to happen here rather than in the router, because
        /// <c>SqlServerSearchService</c> expands a single <c>_type</c> or <c>_lastUpdated</c> key - and an
        /// absent sort - into its two-column form before the router sees the options, at which point the two
        /// lists are no longer comparable.
        /// </para>
        /// </remarks>
        private static bool IgnixaSortMatches(SearchOptions searchOptions)
        {
            IReadOnlyList<IgnixaSortExpression> ignixaSort = searchOptions.IgnixaOptions?.Sort;
            IReadOnlyList<(SearchParameterInfo SearchParameterInfo, SortOrder SortOrder)> legacySort = searchOptions.Sort;

            int ignixaCount = ignixaSort?.Count ?? 0;
            int legacyCount = legacySort?.Count ?? 0;

            if (ignixaCount != legacyCount)
            {
                return false;
            }

            for (int i = 0; i < ignixaCount; i++)
            {
                bool ignixaAscending = ignixaSort[i].SortOrder == IgnixaSortOrder.Ascending;

                if (!string.Equals(ignixaSort[i].Parameter.Code, legacySort[i].SearchParameterInfo?.Code, StringComparison.Ordinal) ||
                    ignixaAscending != (legacySort[i].SortOrder == SortOrder.Ascending))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Appends a wildcard (<c>*</c>) SMART scope's own search parameters to the Ignixa query parameter list,
        /// mirroring what <see cref="CheckFineGrainedAccessControl"/> does to the legacy <c>SearchParams</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A wildcard scope's parameters are not a per-type restriction, so they have no <c>AccessConstraint</c>
        /// spelling. Legacy does not treat them as one either: it appends them to the request's own SearchParams,
        /// which are then parsed exactly like ordinary query parameters. Doing the same on the Ignixa side is the
        /// faithful translation rather than an approximation of one - the parameters bind against the same
        /// resource types, through the same binder, and are reported on the bundle the same way when unsupported,
        /// which keeps the router's unsupported-parameter drop-set comparison meaningful.
        /// </para>
        /// <para>
        /// Runs before <c>IgnixaOptions</c> is built, which is the only point at which parameters can still be
        /// added; by the time <see cref="TranslateClinicalScopesForIgnixa"/> runs the expression already exists.
        /// Only the first wildcard restriction contributes, because legacy breaks out of its scope loop there.
        /// </para>
        /// </remarks>
        private void AppendWildcardScopeParametersForIgnixa(List<Tuple<string, string>> ignixaQueryParameters)
        {
            AccessControlContext accessControl = _contextAccessor.RequestContext?.AccessControlContext;

            if (accessControl?.ApplyFineGrainedAccessControl != true)
            {
                return;
            }

            ScopeRestriction wildcard = accessControl.AllowedResourceActions?
                .FirstOrDefault(restriction => restriction.Resource == KnownResourceTypes.All);

            if (wildcard?.SearchParameters?.Parameters == null)
            {
                return;
            }

            foreach (Tuple<string, string> parameter in wildcard.SearchParameters.Parameters)
            {
                ignixaQueryParameters.Add(parameter);
            }
        }

        /// <summary>
        /// Parses one SMART v2 scope's search parameters into a single Ignixa predicate for that scope's resource
        /// type, or <see langword="null"/> when any part of it could not be parsed.
        /// </summary>
        /// <remarks>
        /// Reuses the same adapter the request's own parameters go through, so a scope predicate and an ordinary
        /// filter are parsed by identical code rather than by a second, divergent path. A parameter Ignixa reports
        /// as unsupported - or one that produces no expression at all - yields <see langword="null"/> rather than
        /// a partial predicate: silently dropping a term from an access control restriction widens it.
        /// </remarks>
        private Ignixa.Search.Expressions.Expression BuildIgnixaScopePredicate(ScopeRestriction restriction)
        {
            Ignixa.Search.Models.SearchOptions scopeOptions;

            try
            {
                scopeOptions = _ignixaSearchOptionsAdapter.Build(
                    restriction.Resource,
                    restriction.SearchParameters.Parameters.ToList(),
                    _ignixaSearchTenantAccessor.TenantId);
            }
            catch (Exception)
            {
                return null;
            }

            if (scopeOptions?.Expression == null || scopeOptions.UnsupportedParams?.Count > 0)
            {
                return null;
            }

            return scopeOptions.Expression;
        }

        private static void ValidateTotalType(TotalType totalType)
        {
            // Estimate is not yet supported.
            if (totalType == TotalType.Estimate)
            {
                throw new SearchOperationNotSupportedException(string.Format(Core.Resources.UnsupportedTotalParameter, totalType, SupportedTotalTypes));
            }
        }

        private Expression LegacyExpressionProjection(string[] resourceTypesString, string name, string value)
        {
            return _expressionParser.Parse(resourceTypesString, name, value);
        }

        /// <summary>
        /// Builds the Ignixa form of a SMART user compartment and ANDs it into the Ignixa expression, returning
        /// whether the whole membership rule was expressed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This mirrors <c>SmartCompartmentSearchRewriter</c>, which admits a resource for any of three reasons:
        /// it refers to the SMART user (ordinary compartment membership), it <em>is</em> the SMART user's own
        /// resource, or it is a "universal" type that belongs to no compartment and is visible to everyone. The
        /// three are alternatives, so they combine as a union of row-producing legs rather than an OR of values
        /// of one parameter - each leg reads a different table.
        /// </para>
        /// <para>
        /// Every leg carries its own <c>_type</c> predicate rather than relying on the leg's position. Union legs
        /// lower inside the ambient resource type's scope, so a leg that named no type would be read as a filter
        /// on whatever type is being searched: the "devices with no patient reference" leg in particular would
        /// then admit <em>every</em> resource of the searched type, since no Observation carries Device.patient.
        /// Pairing each such leg with <c>_type</c> makes it collapse to the empty set outside its own type.
        /// </para>
        /// <para>
        /// Returns false, leaving the request on the legacy path, when the Device.patient search parameter the
        /// device restriction needs is not defined by the FHIR version in use (R5 has no such parameter). The
        /// legacy rewriter silently drops the restriction in that case and treats Device as universal, which this
        /// could mirror - but a security narrowing that silently becomes a widening is not worth the parity.
        /// </para>
        /// </remarks>
        private bool TryAppendIgnixaSmartCompartmentExpression(
            SearchOptions searchOptions,
            string compartmentType,
            string compartmentId,
            IReadOnlyCollection<string> resourceTypesString)
        {
            if (searchOptions.IgnixaOptions == null)
            {
                return false;
            }

            var legs = new List<Ignixa.Search.Expressions.Expression>
            {
                new Ignixa.Search.Expressions.CompartmentSearchExpression(
                    compartmentType,
                    compartmentId,
                    IgnixaCompartmentResourceTypes(resourceTypesString)),
                Ignixa.Search.Expressions.Expression.And(
                    IgnixaResourceColumnEquals(IgnixaIdSearchParameter, compartmentId),
                    IgnixaResourceColumnEquals(IgnixaTypeSearchParameter, compartmentType)),
            };

            bool restrictDevices = _featureConfiguration.EnableSmartCompartmentDeviceRestriction &&
                _searchParameterDefinitionManager.TryGetSearchParameter(KnownResourceTypes.Device, DevicePatientSearchParameterCode, out _);

            if (_featureConfiguration.EnableSmartCompartmentDeviceRestriction && !restrictDevices)
            {
                return false;
            }

            var universalResourceTypes = new List<string>
            {
                KnownResourceTypes.Location,
                KnownResourceTypes.Organization,
                KnownResourceTypes.Practitioner,
                KnownResourceTypes.Medication,
            };

            if (!restrictDevices)
            {
                universalResourceTypes.Add(KnownCompartmentTypes.Device);
            }

            // A _type filter narrows which universal types can still appear. DomainResource is the default
            // stand-in the rewriter uses for "no filter", so it does not count as one.
            bool hasResourceTypeFilter = resourceTypesString?.Any(resourceType => !string.Equals(resourceType, KnownResourceTypes.DomainResource, StringComparison.Ordinal)) == true;
            if (hasResourceTypeFilter)
            {
                universalResourceTypes = universalResourceTypes.Where(resourceTypesString.Contains).ToList();
            }

            // One leg per type rather than a single `_type=a,b,c`: the compiler's resource-column rule lowers a
            // single equality, and a union of equalities is the same set with no new vocabulary needed.
            foreach (string universalResourceType in universalResourceTypes)
            {
                legs.Add(IgnixaResourceColumnEquals(IgnixaTypeSearchParameter, universalResourceType));
            }

            if (restrictDevices && (!hasResourceTypeFilter || resourceTypesString.Contains(KnownResourceTypes.Device, StringComparer.Ordinal)))
            {
                // Devices with no patient reference at all are visible in every SMART compartment. `:missing=true`
                // on the reference parameter is exactly the legacy NotReferencingExpression's "no outgoing
                // reference for this parameter", expressed in vocabulary Ignixa already lowers.
                Ignixa.Search.Expressions.MultiaryExpression orphanDevices =
                    BuildIgnixaDeviceLeg(new[] { Tuple.Create($"{DevicePatientSearchParameterCode}:missing", "true") });
                if (orphanDevices == null)
                {
                    return false;
                }

                legs.Add(orphanDevices);

                if (string.Equals(compartmentType, KnownResourceTypes.Patient, StringComparison.Ordinal))
                {
                    Ignixa.Search.Expressions.MultiaryExpression ownDevices =
                        BuildIgnixaDeviceLeg(new[] { Tuple.Create(DevicePatientSearchParameterCode, $"{compartmentType}/{compartmentId}") });
                    if (ownDevices == null)
                    {
                        return false;
                    }

                    legs.Add(ownDevices);
                }
            }

            Ignixa.Search.Expressions.Expression membership =
                Ignixa.Search.Expressions.Expression.Union(Ignixa.Search.Expressions.UnionOperator.All, legs);

            searchOptions.IgnixaOptions.Expression = searchOptions.IgnixaOptions.Expression == null
                ? membership
                : Ignixa.Search.Expressions.Expression.And(searchOptions.IgnixaOptions.Expression, membership);

            return true;
        }

        private static Ignixa.Search.Expressions.SearchParameterExpression IgnixaResourceColumnEquals(Ignixa.Search.Models.SearchParameterInfo parameter, string value)
            => new Ignixa.Search.Expressions.SearchParameterExpression(
                parameter,
                new Ignixa.Search.Expressions.SearchParameterPredicateExpression(
                    parameter,
                    Ignixa.Specification.ValueSets.Normative.SearchComparator.Eq,
                    modifier: null,
                    new Ignixa.Search.Indexing.SearchValues.TokenSearchValue(system: null, code: value, text: null)));

        /// <summary>
        /// Builds one Device union leg from a query string fragment, paired with its own <c>_type=Device</c>
        /// predicate, or <see langword="null"/> when the fragment could not be bound.
        /// </summary>
        /// <remarks>
        /// The fragment goes through the same adapter as the request's own parameters rather than being
        /// hand-assembled, so the reference and <c>:missing</c> shapes are exactly the ones the compiler already
        /// has lowering rules for. The <c>_type</c> pairing is what confines the leg to Device: see the union
        /// scoping note on <see cref="TryAppendIgnixaSmartCompartmentExpression"/>.
        /// </remarks>
        private Ignixa.Search.Expressions.MultiaryExpression BuildIgnixaDeviceLeg(IReadOnlyList<Tuple<string, string>> parameters)
        {
            Ignixa.Search.Models.SearchOptions deviceOptions;

            try
            {
                deviceOptions = _ignixaSearchOptionsAdapter.Build(
                    KnownResourceTypes.Device,
                    parameters,
                    _ignixaSearchTenantAccessor.TenantId);
            }
            catch (Exception)
            {
                return null;
            }

            if (deviceOptions?.Expression == null || deviceOptions.UnsupportedParams?.Count > 0)
            {
                return null;
            }

            return Ignixa.Search.Expressions.Expression.And(
                IgnixaResourceColumnEquals(IgnixaTypeSearchParameter, KnownResourceTypes.Device),
                deviceOptions.Expression);
        }

        /// <summary>
        /// The resource-type filter to hand Ignixa's compartment lowering, or <see langword="null"/> when the
        /// request carries no type filter at all.
        /// </summary>
        /// <remarks>
        /// A system-level or wildcard request parses to the single stand-in <c>DomainResource</c>, which the
        /// legacy rewriters read as "no filter". Ignixa has no such convention: it intersects the compartment's
        /// membership groups with whatever type names it is given, and no group contains <c>DomainResource</c>,
        /// so passing the stand-in through collapses the compartment leg to <c>1 = 0</c> and silently drops
        /// every resource that is in the compartment only by reference. Translating the stand-in back to "no
        /// filter" here keeps the legacy convention from leaking into Ignixa's vocabulary.
        /// </remarks>
        private static HashSet<string> IgnixaCompartmentResourceTypes(IReadOnlyCollection<string> resourceTypesString)
            => resourceTypesString == null || resourceTypesString.All(resourceType => string.Equals(resourceType, KnownResourceTypes.DomainResource, StringComparison.Ordinal))
                ? null
                : resourceTypesString.ToHashSet();

        private static void AppendIgnixaCompartmentExpression(SearchOptions searchOptions, string compartmentType, string compartmentId, IReadOnlyCollection<string> resourceTypesString)
        {
            if (searchOptions.IgnixaOptions == null)
            {
                return;
            }

            var compartmentExpression = new Ignixa.Search.Expressions.CompartmentSearchExpression(
                compartmentType,
                compartmentId,
                IgnixaCompartmentResourceTypes(resourceTypesString));

            if (ContainsIgnixaExpression(searchOptions.IgnixaOptions.Expression, compartmentExpression))
            {
                return;
            }

            searchOptions.IgnixaOptions.Expression = searchOptions.IgnixaOptions.Expression == null
                ? compartmentExpression
                : Ignixa.Search.Expressions.Expression.And(searchOptions.IgnixaOptions.Expression, compartmentExpression);
        }

        private static bool ContainsIgnixaExpression(Ignixa.Search.Expressions.Expression expression, Ignixa.Search.Expressions.Expression expected)
        {
            if (expression == null)
            {
                return false;
            }

            if (expression.ValueInsensitiveEquals(expected))
            {
                return true;
            }

            return expression is Ignixa.Search.Expressions.MultiaryExpression multiaryExpression &&
                multiaryExpression.Expressions.Any(item => ContainsIgnixaExpression(item, expected));
        }

        private void AddIgnixaBundleIssues(Ignixa.Search.Models.SearchOptions ignixaOptions)
        {
            if (ignixaOptions?.BundleIssues == null)
            {
                return;
            }

            foreach (Ignixa.Search.Models.IssueComponent issue in ignixaOptions.BundleIssues)
            {
                _contextAccessor.RequestContext?.BundleIssues.Add(new OperationOutcomeIssue(
                    issue.Severity,
                    issue.Code,
                    issue.Diagnostics));
            }
        }

        private void LogExpresssionSearchParameters(Expression expression)
        {
            if (expression == null)
            {
                return;
            }
            else if (expression is SearchParameterExpression baseSearchParameterExpression)
            {
                LogSearchParameterData(baseSearchParameterExpression.Parameter.Url);
                LogExpresssionSearchParameters(baseSearchParameterExpression.Expression);
            }
            else if (expression is SearchParameterExpressionBase baseExpression)
            {
                LogSearchParameterData(baseExpression.Parameter.Url);
            }
            else if (expression is MissingSearchParameterExpression missingSearchParameterExpression)
            {
                LogSearchParameterData(missingSearchParameterExpression.Parameter.Url, missingSearchParameterExpression.IsMissing);
            }
            else if (expression is ChainedExpression chainedExpression)
            {
                LogSearchParameterData(chainedExpression.ReferenceSearchParameter.Url);
                LogExpresssionSearchParameters(chainedExpression.Expression);
            }
            else if (expression is SearchParameterExpression searchParameterExpression)
            {
                LogSearchParameterData(searchParameterExpression.Parameter.Url);
                LogExpresssionSearchParameters(searchParameterExpression.Expression);
            }
            else if (expression is MultiaryExpression multiaryExpression)
            {
                foreach (var subExpression in multiaryExpression.Expressions)
                {
                    LogExpresssionSearchParameters(subExpression);
                }
            }
            else if (expression is UnionExpression unionExpression)
            {
                foreach (var subExpression in unionExpression.Expressions)
                {
                    LogExpresssionSearchParameters(subExpression);
                }
            }
            else if (expression is NotExpression notExpression)
            {
                LogExpresssionSearchParameters(notExpression.Expression);
            }
            else if (expression is SortExpression sortExpression)
            {
                LogSearchParameterData(sortExpression.Parameter.Url);
            }
            else if (expression is IncludeExpression includeExpression)
            {
                LogSearchParameterData(includeExpression.ReferenceSearchParameter?.Url);
            }
        }

        private void LogSearchParameterData(Uri url, bool isMissing = false)
        {
            string logOutput = string.Format("SearchParameters in search. Url: {0}.", url?.OriginalString);

            if (isMissing)
            {
                logOutput = logOutput + string.Format(" IsMissing: {0}.", isMissing);
            }

            _logger.LogInformation(logOutput);
        }

        private void CheckFineGrainedAccessControl(List<Expression> searchExpressions, SearchParams searchParams, List<string> requiredResourceTypes)
        {
            // check resource type restrictions from SMART clinical scopes
            if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true)
            {
                bool allowAllResourceTypes = false;
                var clinicalScopeResources = new List<ResourceType>();
                var finalSmartSearchExpressions = new List<Expression>();
                bool isFineGrainedAccessControlWithSearchParameters = false;

                foreach (ScopeRestriction restriction in _contextAccessor.RequestContext?.AccessControlContext.AllowedResourceActions)
                {
                    if (restriction.Resource == KnownResourceTypes.All)
                    {
                        allowAllResourceTypes = true;

                        // Check if SMART V2 search parameter constraint exists
                        // If yes then we can add it to searchParams before breaking
                        // This should get ANDed with main query and be applied as a common search parameter across all resource types
                        if (restriction.SearchParameters != null && restriction.SearchParameters.Parameters.Any())
                        {
                            // Throw 400 if chained, include or revinclude in searchParameters with ApplyFineGrainedAccessControlWithSearchParameters
                            if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControlWithSearchParameters == true)
                            {
                                bool containsComplexParam = restriction.SearchParameters.Parameters.Any(param => ExpressionParser.ContainsChainOrReverseParameter(param.Item1));
                                if (containsComplexParam || restriction.SearchParameters.Include.Any() || restriction.SearchParameters.RevInclude.Any())
                                {
                                    throw new BadRequestException(string.Format(Core.Resources.IncludeRevIncludeChainedSearchesDoNotSupportFinerGrainedResourceConstraintsUsingSearchParameters));
                                }
                            }

                            foreach (var param in restriction.SearchParameters.Parameters)
                            {
                               searchParams.Add(param.Item1, param.Item2);
                            }
                        }

                        break;
                    }

                    if (!Enum.TryParse<ResourceType>(restriction.Resource, out var clinicalScopeResourceType))
                    {
                        throw new ResourceNotSupportedException(restriction.Resource);
                    }

                    if (!requiredResourceTypes.Contains(KnownResourceTypes.DomainResource) && !requiredResourceTypes.Contains(restriction.Resource))
                    {
                        // For a system level search requiredResourceTypes will have DomainResource as default. For system level search we need to apply all clinical scope restrictions.
                        // Not a system level search and the scope restricted resource type is not a required resource type then do not add the scope restriction
                        continue;
                    }

                    // Form the AND expression for resource type and its searchParameters restrictions.
                    var smartSearchExpressions = new List<Expression>();

                    // Check if there are any search parameter constraint for this clinicalScopeResourceType
                    // If search parameters are defined in the restriction, add them to searchParams.
                    if (restriction.SearchParameters != null && restriction.SearchParameters.Parameters.Any())
                    {
                        // Throw 400 if chained, include or revinclude in searchParameters with ApplyFineGrainedAccessControlWithSearchParameters
                        if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControlWithSearchParameters == true)
                        {
                            bool containsComplexParam = restriction.SearchParameters.Parameters.Any(param => ExpressionParser.ContainsChainOrReverseParameter(param.Item1));
                            if (containsComplexParam || restriction.SearchParameters.Include.Any() || restriction.SearchParameters.RevInclude.Any())
                            {
                                throw new BadRequestException(string.Format(Core.Resources.IncludeRevIncludeChainedSearchesDoNotSupportFinerGrainedResourceConstraintsUsingSearchParameters));
                            }
                        }

                        isFineGrainedAccessControlWithSearchParameters = true;
                        var andedSmartSmartSearchExpressions = new List<Expression>();
                        foreach (var param in restriction.SearchParameters.Parameters)
                        {
                            var fineGrainedSmartSearchExpressions = new List<Expression>();
                            fineGrainedSmartSearchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, clinicalScopeResourceType.ToString(), false)));

                            // We need to parse the search parameters for each resource type since the same search parameter can have different definitions for different resource types
                            var smartSearchParams = new SearchParams();
                            smartSearchParams.Add(param.Item1, param.Item2);
                            fineGrainedSmartSearchExpressions.AddRange(smartSearchParams.Parameters.Select(
                                q =>
                                {
                                    try
                                    {
                                        return LegacyExpressionProjection(new[] { clinicalScopeResourceType.ToString() }, q.Item1, q.Item2);
                                    }
                                    catch (SearchParameterNotSupportedException)
                                    {
                                        return null;
                                    }
                                })
                                .Where(item => item != null));
                            var individualAndExp = Expression.And(fineGrainedSmartSearchExpressions.ToArray());
                            individualAndExp.IsSmartV2UnionExpressionForScopesSearchParameters = true;
                            andedSmartSmartSearchExpressions.Add(individualAndExp);
                        }

                        var andExp = Expression.And(andedSmartSmartSearchExpressions.ToArray());
                        andExp.IsSmartV2UnionExpressionForScopesSearchParameters = true;
                        finalSmartSearchExpressions.Add(andExp);
                    }
                    else
                    {
                        smartSearchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, clinicalScopeResourceType.ToString(), false)));
                        finalSmartSearchExpressions.Add(Expression.And(smartSearchExpressions.ToArray()));
                    }

                    clinicalScopeResources.Add(clinicalScopeResourceType);
                }

                if (!allowAllResourceTypes)
                {
                    // We are applying smart scopes only for the resource types that are requested in the search
                    // i.e. if the search is for /Observation, then we should only apply smart scopes for the Observation type
                    // i.e. if the search is for /Observation?_include=Observation:subject, then we should only apply smart scopes for the Observation and Patient type
                    // i.e. if the search is for /_type=Observation,Practitioner then we should only apply smart scopes for the Observation and Practitioner type
                    if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControlWithSearchParameters == true && finalSmartSearchExpressions.Any())
                    {
                        // Check if any scopes with search parameters were present
                        // If yes then, go ahead with the Union expression
                        // If not then, we can simply use clinicalScopeResources
                        if (isFineGrainedAccessControlWithSearchParameters)
                        {
                            // Builds the search expression like (((ResourceType = A AND <search params 1 for A>) AND (ResourceType = A AND <search params 2 for A>)) OR ((ResourceType = B AND <search params 1 for B>) AND (ResourceType = B AND <search params 2 for B>)))
                            var unionExpr = Expression.Union(UnionOperator.All, finalSmartSearchExpressions);
                            unionExpr.IsSmartV2UnionExpressionForScopesSearchParameters = true;
                            searchExpressions.Add(unionExpr);
                        }
                        else if (clinicalScopeResources.Any())
                        {
                            if (clinicalScopeResources.Count == 1)
                            {
                                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, clinicalScopeResources[0].ToString(), false)));
                            }
                            else
                            {
                                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.In(FieldName.TokenCode, null, clinicalScopeResources)));
                            }
                        }
                    }
                    else
                    {
                        // If ApplyFineGrainedAccessControlWithSearchParameters is false, we only filter by resource type and use format like (ResourceType in (A, B))
                        if (clinicalScopeResources.Any())
                        {
                            if (clinicalScopeResources.Count == 1)
                            {
                                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, clinicalScopeResources[0].ToString(), false)));
                            }
                            else
                            {
                                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.In(FieldName.TokenCode, null, clinicalScopeResources)));
                            }
                        }
                        else // block all queries
                        {
                            searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, "none", false)));
                        }
                    }
                }
            }
        }
    }
}
