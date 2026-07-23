// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Ignixa.Abstractions;
using Ignixa.Search.Parsing;

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1201 // Elements should appear in the correct order
#pragma warning disable SA1649 // File name should match first type name

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Converts FHIR Server query parameters into Ignixa search options.
    /// </summary>
    public class IgnixaSearchOptionsAdapter : IIgnixaSearchOptionsAdapter
    {
        private readonly ISearchOptionsBuilderFactory _searchOptionsBuilderFactory;
        private readonly IFhirSchemaProvider _schemaProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSearchOptionsAdapter"/> class.
        /// </summary>
        /// <param name="searchOptionsBuilderFactory">The Ignixa search options builder factory.</param>
        /// <param name="schemaProvider">The Ignixa FHIR schema provider.</param>
        public IgnixaSearchOptionsAdapter(ISearchOptionsBuilderFactory searchOptionsBuilderFactory, IFhirSchemaProvider schemaProvider)
        {
            EnsureArg.IsNotNull(searchOptionsBuilderFactory, nameof(searchOptionsBuilderFactory));
            EnsureArg.IsNotNull(schemaProvider, nameof(schemaProvider));

            _searchOptionsBuilderFactory = searchOptionsBuilderFactory;
            _schemaProvider = schemaProvider;
        }

        /// <inheritdoc />
        public Ignixa.Search.Models.SearchOptions Build(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            int? tenantId)
        {
            var ignixaQueryParameters = (queryParameters ?? Array.Empty<Tuple<string, string>>())
                .Select(ToIgnixaQueryParameter)
                .ToArray();

            ISearchOptionsBuilder builder = _searchOptionsBuilderFactory.Create(IgnixaFhirVersionAdapter.Current, tenantId);
            return builder.Build(resourceType, ignixaQueryParameters, _schemaProvider, new List<ParameterTrace>());
        }

        private static QueryParameter ToIgnixaQueryParameter(Tuple<string, string> queryParameter)
        {
            EnsureArg.IsNotNull(queryParameter, nameof(queryParameter));

            if (queryParameter.Item1 == null)
            {
                throw new ArgumentException("Search query parameter name cannot be null.", nameof(queryParameter));
            }

            if (queryParameter.Item2 == null)
            {
                throw new ArgumentException("Search query parameter value cannot be null.", nameof(queryParameter));
            }

            return new QueryParameter(queryParameter.Item1, queryParameter.Item2);
        }
    }

    /// <summary>
    /// Builds Ignixa search options for decoded FHIR query parameters.
    /// </summary>
    public interface IIgnixaSearchOptionsAdapter
    {
        /// <summary>
        /// Builds Ignixa search options.
        /// </summary>
        /// <param name="resourceType">The requested FHIR resource type.</param>
        /// <param name="queryParameters">The decoded FHIR query parameters.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <returns>The Ignixa search options.</returns>
        Ignixa.Search.Models.SearchOptions Build(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            int? tenantId);
    }

    /// <summary>
    /// Creates Ignixa search option builders for the compiled server version.
    /// </summary>
    public class IgnixaSearchOptionsBuilderFactory : ISearchOptionsBuilderFactory
    {
        private readonly Ignixa.Search.Expressions.Parsers.IExpressionParser _expressionParser;
        private readonly Ignixa.Search.Definition.ISearchParameterDefinitionManager _searchParameterDefinitionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSearchOptionsBuilderFactory"/> class.
        /// </summary>
        /// <param name="expressionParser">The Ignixa expression parser.</param>
        /// <param name="searchParameterDefinitionManager">The Ignixa search parameter definition manager.</param>
        public IgnixaSearchOptionsBuilderFactory(
            Ignixa.Search.Expressions.Parsers.IExpressionParser expressionParser,
            Ignixa.Search.Definition.ISearchParameterDefinitionManager searchParameterDefinitionManager)
        {
            EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
            EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));

            _expressionParser = expressionParser;
            _searchParameterDefinitionManager = searchParameterDefinitionManager;
        }

        /// <inheritdoc />
        public ISearchOptionsBuilder Create(Ignixa.Abstractions.FhirVersion fhirVersion)
        {
            return Create(fhirVersion, tenantId: null);
        }

        /// <inheritdoc />
        public ISearchOptionsBuilder Create(Ignixa.Abstractions.FhirVersion fhirVersion, int? tenantId)
        {
            return new SearchOptionsBuilder(_expressionParser, _searchParameterDefinitionManager);
        }
    }
}
