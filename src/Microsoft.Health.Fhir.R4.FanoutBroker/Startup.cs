// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Conformance;
using Microsoft.Health.Fhir.FanoutBroker.Features.Health;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker
{
    /// <summary>
    /// Startup configuration for the FHIR Fanout Broker service.
    /// </summary>
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        /// <summary>
        /// Configure services for dependency injection.
        /// </summary>
        public void ConfigureServices(IServiceCollection services)
        {
            // Add configuration
            services.Configure<FanoutBrokerConfiguration>(
                Configuration.GetSection("FanoutBroker"));

            // Add core services
            services.AddControllers();
            services.AddHttpClient();
            services.AddLogging();

            // Add fanout-specific services
            services.AddScoped<IExecutionStrategyAnalyzer, ExecutionStrategyAnalyzer>();
            services.AddScoped<IFhirServerOrchestrator, FhirServerOrchestrator>();
            services.AddScoped<IResultAggregator, ResultAggregator>();
            services.AddScoped<IChainedSearchProcessor, ChainedSearchProcessor>();
            services.AddScoped<IIncludeProcessor, IncludeProcessor>();
            services.AddScoped<ISearchOptionsFactory, FanoutSearchOptionsFactory>();
            services.AddScoped<IConformanceProvider, FanoutCapabilityStatementProvider>();
            services.AddScoped<ISearchService, FanoutSearchService>();
            services.AddSingleton<IResourceDeserializer, ResourceDeserializer>();
            AddSerializers(services);

            // Add health checks
            services.AddHealthChecks()
                .AddCheck<FanoutBrokerHealthCheck>("fanout-broker");
        }

        private void AddSerializers(IServiceCollection services)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var jsonParser = new FhirJsonParser(new ParserSettings() { PermissiveParsing = true, TruncateDateTimeToDate = true });
#pragma warning restore CS0618 // Type or member is obsolete
            var jsonSerializer = new FhirJsonSerializer();

            var xmlParser = new FhirXmlParser();
            var xmlSerializer = new FhirXmlSerializer();

            services.AddSingleton(jsonParser);
            services.AddSingleton(jsonSerializer);
            services.AddSingleton(xmlParser);
            services.AddSingleton(xmlSerializer);
            // services.AddSingleton<BundleSerializer>();

            FhirPathCompiler.DefaultSymbolTable.AddFhirExtensions();

            ResourceElement SetMetadata(Resource resource, string versionId, DateTimeOffset lastModified)
            {
                resource.VersionId = versionId;
                resource.Meta.LastUpdated = lastModified;

                return resource.ToResourceElement();
            }

            services.AddSingleton<IReadOnlyDictionary<FhirResourceFormat, Func<Resource, string>>>(
            provider =>
            {
                var jsonSerializer = provider.GetRequiredService<FhirJsonSerializer>();
                var xmlSerializer = provider.GetRequiredService<FhirXmlSerializer>();

                return new Dictionary<FhirResourceFormat, Func<Resource, string>>
                {
                    {
                        FhirResourceFormat.Json, resource => jsonSerializer.SerializeToString(resource)
                    },
                    {
                        FhirResourceFormat.Xml, resource => xmlSerializer.SerializeToString(resource)
                    },
                };
            });

            services.Add<ResourceSerializer>()
                    .Singleton()
                    .AsSelf()
                    .AsService<IResourceSerializer>();

            services.AddSingleton<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>(_ =>
            {
                return new Dictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>
                {
                    {
                        FhirResourceFormat.Json, (str, version, lastModified) =>
                        {
                            var resource = jsonParser.Parse<Resource>(str);
                            return SetMetadata(resource, version, lastModified);
                        }
                    },
                    {
                        FhirResourceFormat.Xml, (str, version, lastModified) =>
                        {
                            var resource = xmlParser.Parse<Resource>(str);

                            return SetMetadata(resource, version, lastModified);
                        }
                    },
                };
            });

            services.Add<ResourceDeserializer>()
                    .Singleton()
                    .AsSelf()
                    .AsService<IResourceDeserializer>();
        }

        /// <summary>
        /// Configure the HTTP request pipeline.
        /// </summary>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHealthChecks("/health");
            });
        }
    }
}
