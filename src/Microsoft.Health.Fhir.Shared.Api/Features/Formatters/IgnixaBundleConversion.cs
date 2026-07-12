// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.Api.Features.Formatters
{
    /// <summary>
    /// Shared conversion helper for <see cref="IgnixaRawBundle"/> round-tripping.
    /// </summary>
    internal static class IgnixaBundleConversion
    {
        private static readonly FhirJsonParser Parser = new();

        /// <summary>
        /// Converts an <see cref="IgnixaRawBundle"/> to a Firely <see cref="Hl7.Fhir.Model.Bundle"/> POCO by
        /// round-tripping it through <see cref="IgnixaBundleSerializer"/> and re-parsing with <see cref="FhirJsonParser"/>.
        /// This is the only supported way to materialize all entries in the carrier.
        /// </summary>
        /// <param name="serializer">The Ignixa bundle serializer.</param>
        /// <param name="rawBundle">The raw bundle to convert.</param>
        /// <returns>A Firely Bundle POCO with all entries materialized.</returns>
        internal static async Task<Hl7.Fhir.Model.Bundle> ToFirelyBundleAsync(IgnixaBundleSerializer serializer, IgnixaRawBundle rawBundle)
        {
            using var stream = new MemoryStream();
            await serializer.Serialize(rawBundle, stream, pretty: false).ConfigureAwait(false);
            stream.Position = 0;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(reader);
            return await Parser.ParseAsync<Hl7.Fhir.Model.Bundle>(jsonReader).ConfigureAwait(false);
        }
    }
}
