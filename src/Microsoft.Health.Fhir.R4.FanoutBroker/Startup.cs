// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Extensions;
using Microsoft.Health.Fhir.FanoutBroker.Features.Conformance;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Health;
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
            services.AddScoped<IConformanceProvider, FanoutCapabilityStatementProvider>();
            services.AddScoped<ISearchService, FanoutSearchService>();

            // Add health checks
            services.AddHealthChecks()
                .AddCheck<FanoutBrokerHealthCheck>("fanout-broker");

            // Add Swagger/OpenAPI
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = "FHIR Fanout Broker API",
                    Version = "v1",
                    Description = "Read-only FHIR service that aggregates search queries across multiple FHIR servers"
                });
            });
        }

        /// <summary>
        /// Configure the HTTP request pipeline.
        /// </summary>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "FHIR Fanout Broker API v1");
                    c.RoutePrefix = string.Empty; // Swagger UI at root
                });
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