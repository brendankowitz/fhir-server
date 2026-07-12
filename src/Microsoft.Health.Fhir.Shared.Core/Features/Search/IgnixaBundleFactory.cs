// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EnsureThat;
using Ignixa.Serialization.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Telemetry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Ignixa-native counterpart to <see cref="BundleFactory"/>. Ports the same link/entry/search-mode/
    /// history-verb construction logic onto Ignixa's <see cref="BundleJsonNode"/> node family instead of
    /// Firely POCOs, producing an <see cref="Resources.Bundle.IgnixaRawBundle"/> wrapped in a
    /// <see cref="ResourceElement"/>. Every decision this class makes must match <see cref="BundleFactory"/>
    /// exactly -- this is a port, not a redesign. Not wired into DI; unreachable in production.
    /// </summary>
    public class IgnixaBundleFactory : IBundleFactory
    {
        private readonly IUrlResolver _urlResolver;
        private readonly RequestContextAccessor<IFhirRequestContext> _fhirRequestContextAccessor;
        private readonly IIgnixaSchemaContext _schemaContext;
        private readonly ILogger<IgnixaBundleFactory> _logger;

        public IgnixaBundleFactory(
            IUrlResolver urlResolver,
            RequestContextAccessor<IFhirRequestContext> fhirRequestContextAccessor,
            IIgnixaSchemaContext schemaContext,
            ILogger<IgnixaBundleFactory> logger)
        {
            EnsureArg.IsNotNull(urlResolver, nameof(urlResolver));
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _urlResolver = urlResolver;
            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _schemaContext = schemaContext;
            _logger = logger;
        }

        public ResourceElement CreateSearchBundle(SearchResult result)
        {
            return CreateBundle(result, BundleJsonNode.BundleType.Searchset, r =>
            {
                var rawResource = new RawResourceElement(r.Resource);

                var metadata = new BundleComponentJsonNode
                {
                    FullUrl = _urlResolver.ResolveResourceWrapperUrl(r.Resource).OriginalString,
                    Search = new BundleComponentSearchJsonNode
                    {
                        Mode = r.SearchEntryMode == SearchEntryMode.Match ? "match" : "include",
                    },
                };

                return Resources.Bundle.IgnixaRawBundleEntry.ForRawResource(metadata, rawResource);
            });
        }

        public ResourceElement CreateHistoryBundle(SearchResult result)
        {
            return CreateBundle(result, BundleJsonNode.BundleType.History, r =>
            {
                var rawResource = new RawResourceElement(r.Resource);

                bool hasVerb = TryResolveVerb(r.Resource.Request?.Method, r.Resource.IsDeleted, out string httpVerb);

                var metadata = new BundleComponentJsonNode
                {
                    FullUrl = _urlResolver.ResolveResourceWrapperUrl(r.Resource).OriginalString,
                };

                if (hasVerb)
                {
                    metadata.Request = new BundleComponentRequestJsonNode
                    {
                        Method = httpVerb,
                        Url = $"{r.Resource.ResourceTypeName}/{(httpVerb == "POST" ? null : r.Resource.ResourceId)}",
                    };

                    metadata.Response = new BundleComponentResponseJsonNode
                    {
                        Status = ToStatusString(httpVerb, r.Resource.Version),
                        LastModified = r.Resource.LastModified,
                        Etag = WeakETag.FromVersionId(r.Resource.Version).ToString(),
                    };
                }

                return Resources.Bundle.IgnixaRawBundleEntry.ForRawResource(metadata, rawResource);
            });
        }

        /// <summary>
        /// Resolves a <see cref="ResourceRequest.Method"/> string (or the "Import" pseudo-verb) to the
        /// upper-case HTTP verb literal written into <c>Bundle.entry.request.method</c>. Mirrors
        /// <see cref="BundleFactory.CreateHistoryBundle"/>'s use of <c>Enum.TryParse&lt;Bundle.HTTPVerb&gt;</c>
        /// without depending on the Firely enum: STU3 doesn't support PATCH bundle entries, so it's mapped
        /// to PUT, matching the STU3-only block in the Firely version.
        /// </summary>
        private static bool TryResolveVerb(string method, bool isDeleted, out string verb)
        {
            switch (method?.ToUpperInvariant())
            {
                case "GET":
                case "POST":
                case "PUT":
                case "DELETE":
                    verb = method.ToUpperInvariant();
                    return true;
#if !Stu3
                case "PATCH":
                case "HEAD":
                    verb = method.ToUpperInvariant();
                    return true;
#else
                case "PATCH":
                    // STU3 doesn't have a PATCH verb, so let's map it to PUT.
                    verb = "PUT";
                    return true;
#endif
                case "IMPORT":
                    verb = isDeleted ? "DELETE" : "PUT";
                    return true;
                default:
                    verb = null;
                    return false;
            }
        }

        private static string ToStatusString(string httpVerb, string versionId)
        {
            static string Format(HttpStatusCode statusCode) => $"{(int)statusCode} {statusCode}";

            return httpVerb switch
            {
                "POST" => Format(HttpStatusCode.Created),
                "PUT" when string.Equals(versionId, "1", StringComparison.Ordinal) => Format(HttpStatusCode.Created),
                "PUT" => Format(HttpStatusCode.OK),
                "GET" => Format(HttpStatusCode.OK),
#if !Stu3
                "PATCH" => Format(HttpStatusCode.OK),
                "HEAD" => Format(HttpStatusCode.OK),
#endif
                "DELETE" => Format(HttpStatusCode.NoContent),
                _ => throw new NotImplementedException($"{httpVerb} was not defined."),
            };
        }

        private void CreateLinks(SearchResult result, BundleJsonNode bundle)
        {
            bool problemWithLinks = false;
            if (!_fhirRequestContextAccessor.RequestContext?.RouteName?.Equals(RouteNames.Includes, StringComparison.OrdinalIgnoreCase) ?? true)
            {
                if (result.ContinuationToken != null)
                {
                    try
                    {
                        Uri nextUrl = _urlResolver.ResolveRouteUrl(
                            result.UnsupportedSearchParameters,
                            result.SortOrder,
                            ContinuationTokenEncoder.Encode(result.ContinuationToken),
                            true);
                        AddLink(bundle, "next", nextUrl?.OriginalString);
                    }
                    catch (UriFormatException)
                    {
                        problemWithLinks = true;
                    }
                }

                if (result.IncludesContinuationToken != null)
                {
                    try
                    {
                        var ambientRouteValuesOverride = new Dictionary<string, object>
                        {
                            { KnownHttpRequestProperties.RouteValueAction, "Search" },
                            { KnownHttpRequestProperties.RouteValueController, RouteNames.Includes },
                            { KnownActionParameterNames.ResourceType, _fhirRequestContextAccessor.RequestContext.ResourceType },
                        };

                        Uri url = _urlResolver.ResolveRouteUrl(
                            result.UnsupportedSearchParameters,
                            result.SortOrder,
                            null,
                            true,
                            ContinuationTokenEncoder.Encode(result.IncludesContinuationToken),
                            RouteNames.Includes,
                            ambientRouteValuesOverride);

                        AddLink(bundle, "related", url?.AbsoluteUri);
                    }
                    catch (UriFormatException)
                    {
                        problemWithLinks = true;
                    }
                }
            }
            else
            {
                if (result.IncludesContinuationToken != null)
                {
                    try
                    {
                        Uri nextUrl = _urlResolver.ResolveRouteUrl(
                            result.UnsupportedSearchParameters,
                            result.SortOrder,
                            null,
                            true,
                            ContinuationTokenEncoder.Encode(result.IncludesContinuationToken));
                        AddLink(bundle, "next", nextUrl?.OriginalString);
                    }
                    catch (UriFormatException)
                    {
                        problemWithLinks = true;
                    }
                }
            }

            try
            {
                // Add the self link to indicate which search parameters were used.
                Uri selfUrl = _urlResolver.ResolveRouteUrl(result.UnsupportedSearchParameters, result.SortOrder);
                AddLink(bundle, "self", selfUrl?.OriginalString);
            }
            catch (UriFormatException)
            {
                problemWithLinks = true;
            }

            if (problemWithLinks)
            {
                _fhirRequestContextAccessor.RequestContext.BundleIssues.Add(
                          new OperationOutcomeIssue(
                              OperationOutcomeConstants.IssueSeverity.Warning,
                              OperationOutcomeConstants.IssueType.NotSupported,
                              string.Format(Core.Resources.LinksCantBeCreated)));
            }
        }

        private static void AddLink(BundleJsonNode bundle, string relation, string url)
        {
            if (url == null)
            {
                return;
            }

            bundle.Link.Add(new BundleLinkJsonNode
            {
                Relation = relation,
                Url = url,
            });
        }

        private ResourceElement CreateBundle(SearchResult result, BundleJsonNode.BundleType type, Func<SearchResultEntry, Resources.Bundle.IgnixaRawBundleEntry> selectionFunction)
        {
            EnsureArg.IsNotNull(result, nameof(result));

            // Create the bundle skeleton from the result.
            var skeleton = new BundleJsonNode();
            CreateLinks(result, skeleton);

            var entries = new List<Resources.Bundle.IgnixaRawBundleEntry>();

            if (_fhirRequestContextAccessor.RequestContext.BundleIssues.Any())
            {
                entries.Add(CreateOperationOutcomeEntry(_fhirRequestContextAccessor.RequestContext.BundleIssues, _fhirRequestContextAccessor.RequestContext.CorrelationId));
            }

            if (result.SearchIssues.Any())
            {
                entries.Add(CreateOperationOutcomeEntry(result.SearchIssues, _fhirRequestContextAccessor.RequestContext.CorrelationId));
            }

            entries.AddRange(result.Results.Select(selectionFunction));

            skeleton.Id = _fhirRequestContextAccessor.RequestContext.CorrelationId;
            skeleton.Type = type;
            skeleton.Total = result.TotalCount;
            skeleton.Meta.LastUpdated = Clock.UtcNow;

            var rawBundle = new Resources.Bundle.IgnixaRawBundle(skeleton, entries);
            var ignixaElement = new IgnixaResourceElement(skeleton, _schemaContext.Schema);
            return new ResourceElement(ignixaElement.ToTypedElement(), rawBundle);
        }

        private static Resources.Bundle.IgnixaRawBundleEntry CreateOperationOutcomeEntry(IEnumerable<OperationOutcomeIssue> issues, string correlationId)
        {
            var operationOutcome = new OperationOutcomeJsonNode
            {
                Id = correlationId,
            };

            foreach (OperationOutcomeIssue issue in issues)
            {
                operationOutcome.Issue.Add(ToIgnixaIssueComponent(issue));
            }

            var metadata = new BundleComponentJsonNode
            {
                Search = new BundleComponentSearchJsonNode
                {
                    Mode = "outcome",
                },
            };

            return Resources.Bundle.IgnixaRawBundleEntry.ForConstructedResource(metadata, operationOutcome);
        }

        private static OperationOutcomeJsonNode.IssueComponent ToIgnixaIssueComponent(OperationOutcomeIssue issue)
        {
            var issueComponent = new OperationOutcomeJsonNode.IssueComponent
            {
                Severity = Enum.Parse<OperationOutcomeJsonNode.IssueSeverity>(issue.Severity),
                Code = Enum.Parse<OperationOutcomeJsonNode.IssueType>(issue.Code),
                Diagnostics = issue.Diagnostics,
            };

            if (issue.Expression != null)
            {
                foreach (string expression in issue.Expression)
                {
                    issueComponent.Expression.Add(expression);
                }
            }

            if (issue.DetailsCodes != null || issue.DetailsText != null)
            {
                var details = new CodeableConceptJsonNode
                {
                    Text = issue.DetailsText,
                };

                if (issue.DetailsCodes != null)
                {
                    foreach (Hl7.Fhir.Model.Coding coding in issue.DetailsCodes.Coding)
                    {
                        details.Coding.Add(new CodingJsonNode
                        {
                            System = coding.System,
                            Code = coding.Code,
                            Display = coding.Display,
                        });
                    }
                }

                issueComponent.Details = details;
            }

            return issueComponent;
        }
    }
}
