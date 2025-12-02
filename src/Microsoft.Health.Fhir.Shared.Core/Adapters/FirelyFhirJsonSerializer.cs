// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Health.Fhir.Core.Abstractions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Shared.Core.Adapters
{
    /// <summary>
    /// Adapter implementation of <see cref="IFhirSerializer"/> using the Firely SDK for JSON serialization.
    /// This is a temporary adapter to maintain compatibility during the migration to Ignixa SDK.
    /// </summary>
    public class FirelyFhirJsonSerializer : IFhirSerializer
    {
        private readonly FhirJsonParser _parser;
        private readonly FhirJsonSerializer _serializer;

        public FirelyFhirJsonSerializer(FhirJsonParser parser, FhirJsonSerializer serializer)
        {
            EnsureArg.IsNotNull(parser, nameof(parser));
            EnsureArg.IsNotNull(serializer, nameof(serializer));

            _parser = parser;
            _serializer = serializer;
        }

        /// <inheritdoc />
        public FhirResourceFormat Format => FhirResourceFormat.Json;

        /// <inheritdoc />
        public string SerializeToString(Resource resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            return _serializer.SerializeToString(resource);
        }

        /// <inheritdoc />
        public T Parse<T>(string data)
            where T : Resource
        {
            EnsureArg.IsNotNullOrWhiteSpace(data, nameof(data));
            return _parser.Parse<T>(data);
        }
    }
}
