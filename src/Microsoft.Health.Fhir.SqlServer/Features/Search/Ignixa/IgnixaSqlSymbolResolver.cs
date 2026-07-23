// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Search.Sql.Symbols;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Resolves Ignixa SQL symbol identifiers from the FHIR Server SQL catalog.
    /// </summary>
    /// <remarks>
    /// This resolver maps only the exact <see cref="IgnixaSearchParameterInfo.Url"/> contract.
    /// It does not substitute by code, type, or <see cref="IgnixaSearchParameterInfo.OverridesUrl"/>;
    /// override handling is left to the upstream <see cref="Resolve"/> contract.
    /// </remarks>
    internal sealed class IgnixaSqlSymbolResolver : ISymbolResolver
    {
        private readonly ISqlServerFhirModel _model;

        public IgnixaSqlSymbolResolver(ISqlServerFhirModel model)
        {
            EnsureArg.IsNotNull(model, nameof(model));
            _model = model;
        }

        /// <inheritdoc />
        public Task<short?> GetSearchParamIdAsync(IgnixaSearchParameterInfo parameter, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(parameter, nameof(parameter));

            cancellationToken.ThrowIfCancellationRequested();

            if (parameter.Url is null)
            {
                return Task.FromResult<short?>(null);
            }

            if (_model.TryGetSearchParamId(parameter.Url, out short id))
            {
                return Task.FromResult<short?>(id);
            }

            return Task.FromResult<short?>(null);
        }

        /// <inheritdoc />
        public Task<short?> GetResourceTypeIdAsync(string resourceType, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(resourceType, nameof(resourceType));

            cancellationToken.ThrowIfCancellationRequested();

            if (_model.TryGetResourceTypeId(resourceType, out short id))
            {
                return Task.FromResult<short?>(id);
            }

            return Task.FromResult<short?>(null);
        }
    }
}
