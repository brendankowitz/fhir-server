// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Context;

#pragma warning disable SA1402 // File may only contain a single type

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Reads the Ignixa search tenant identifier from the FHIR request context.
    /// </summary>
    public class IgnixaSearchTenantAccessor
    {
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSearchTenantAccessor"/> class.
        /// </summary>
        /// <param name="contextAccessor">The FHIR request context accessor.</param>
        public IgnixaSearchTenantAccessor(RequestContextAccessor<IFhirRequestContext> contextAccessor)
        {
            EnsureArg.IsNotNull(contextAccessor, nameof(contextAccessor));

            _contextAccessor = contextAccessor;
        }

        /// <summary>
        /// Gets the request tenant identifier when one is present.
        /// </summary>
        public int? TenantId
        {
            get
            {
                if (_contextAccessor.RequestContext?.Properties?.TryGetValue(IgnixaSearchContextPropertyNames.TenantId, out object value) != true)
                {
                    return null;
                }

                return value is int tenantId
                    ? tenantId
                    : throw new InvalidOperationException($"The {IgnixaSearchContextPropertyNames.TenantId} request context property must be an integer.");
            }
        }
    }

    internal static class IgnixaSearchContextPropertyNames
    {
        internal const string TenantId = "Ignixa.Search.TenantId";
    }
}
