// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Core.Configs;

namespace Microsoft.Health.Fhir.Api.Features.Formatters
{
    /// <summary>
    /// Sole owner of MVC's <see cref="TextInputFormatter"/>/<see cref="TextOutputFormatter"/> ordering.
    /// Which formatters are present (Firely, Ignixa, or both) is decided per <see cref="FhirSdkMode"/>
    /// by <c>SdkModeFeatureModule</c>; this class only orders whatever was registered, preserving DI
    /// registration order, and announces the effective mode at startup.
    /// </summary>
    internal class FormatterConfiguration : IPostConfigureOptions<MvcOptions>
    {
        private readonly TextInputFormatter[] _inputFormatters;
        private readonly TextOutputFormatter[] _outputFormatters;
        private readonly FhirSdkMode _sdkMode;
        private readonly ILogger<FormatterConfiguration> _logger;

        public FormatterConfiguration(
            IOptions<FeatureConfiguration> featureConfiguration,
            IOptions<CoreFeatureConfiguration> coreFeatureConfiguration,
            IEnumerable<TextInputFormatter> inputFormatters,
            IEnumerable<TextOutputFormatter> outputFormatters,
            ILogger<FormatterConfiguration> logger)
        {
            EnsureArg.IsNotNull(featureConfiguration, nameof(featureConfiguration));
            EnsureArg.IsNotNull(featureConfiguration.Value, nameof(featureConfiguration));
            EnsureArg.IsNotNull(coreFeatureConfiguration, nameof(coreFeatureConfiguration));
            EnsureArg.IsNotNull(coreFeatureConfiguration.Value, nameof(coreFeatureConfiguration));
            EnsureArg.IsNotNull(inputFormatters, nameof(inputFormatters));
            EnsureArg.IsNotNull(outputFormatters, nameof(outputFormatters));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _inputFormatters = inputFormatters.ToArray();
            _outputFormatters = outputFormatters.ToArray();
            _sdkMode = coreFeatureConfiguration.Value.SdkMode;
            _logger = logger;
        }

        public void PostConfigure(string name, MvcOptions options)
        {
            for (int i = 0; i < _inputFormatters.Length; i++)
            {
                options.InputFormatters.Insert(i, _inputFormatters[i]);
            }

            for (int i = 0; i < _outputFormatters.Length; i++)
            {
                options.OutputFormatters.Insert(i, _outputFormatters[i]);
            }

            // Disable the built-in global UnsupportedContentTypeFilter
            // We enable our own ValidateContentTypeFilterAttribute on the FhirController, the built-in filter
            // short-circuits the response and prevents the operation outcome from being returned.
            var unsupportedContentTypeFilter = options.Filters.Single(x => x is UnsupportedContentTypeFilter);
            options.Filters.Remove(unsupportedContentTypeFilter);

            _logger.LogInformation("FHIR SDK formatter mode: {SdkMode}", _sdkMode);
        }
    }
}
