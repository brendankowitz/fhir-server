// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Net;
using System.Reflection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Api.Features.ActionResults;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.UnitTests.Features.ActionResults
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class FhirResultTests
    {
        [Fact]
        public void GivenAGoneStatus_WhenReturningAResult_ThenTheContentShouldBeEmpty()
        {
            var result = FhirResult.Gone();
            var context = new ActionContext
            {
                HttpContext = new DefaultHttpContext(),
            };

            result.ExecuteResult(context);

            Assert.Null(result.Result);
            Assert.Equal(HttpStatusCode.Gone, result.StatusCode.GetValueOrDefault());
            Assert.Equal(0, context.HttpContext.Request.Body.Length);
        }

        [Fact]
        public void GivenANoContentStatus_WhenReturningAResult_ThenTheStatusCodeIsSetCorrectly()
        {
            var result = FhirResult.NoContent();
            var context = new ActionContext
            {
                HttpContext = new DefaultHttpContext(),
            };

            result.ExecuteResult(context);

            Assert.Null(result.Result);
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode.GetValueOrDefault());
        }

        [Fact]
        public void GivenANotFoundStatus_WhenReturningAResult_ThenTheStatusCodeIsSetCorrectly()
        {
            var result = FhirResult.NotFound();
            var context = new ActionContext
            {
                HttpContext = new DefaultHttpContext(),
            };

            result.ExecuteResult(context);

            Assert.Null(result.Result);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode.GetValueOrDefault());
        }

        [Fact]
        public async Task GivenAFhirResult_WhenHeadersThatAlreadyExistsInResponseArePassed_ThenDuplicteHeadersAreRemoved()
        {
            var result = FhirResult.Gone();
            var context = new ActionContext
            {
                HttpContext = new DefaultHttpContext(),
            };

            IActionResultExecutor<ObjectResult> executor = Substitute.For<IActionResultExecutor<ObjectResult>>();
            executor.ExecuteAsync(Arg.Any<ActionContext>(), Arg.Any<ObjectResult>()).ReturnsForAnyArgs(Task.CompletedTask);

            ServiceCollection collection = new ServiceCollection();
            collection.AddSingleton<IActionResultExecutor<ObjectResult>>(executor);
            RequestContextAccessor<IFhirRequestContext> contextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            collection.AddSingleton<RequestContextAccessor<IFhirRequestContext>>(contextAccessor);

            ServiceProvider provider = collection.BuildServiceProvider();
            context.HttpContext.RequestServices = provider;

            result.Headers["testKey1"] = "3";
            result.Headers["testKey2"] = "2";
            context.HttpContext.Response.Headers["testKey2"] = "1";

            await result.ExecuteResultAsync(context);

            Assert.Null(result.Result);
            Assert.Equal(HttpStatusCode.Gone, result.StatusCode.GetValueOrDefault());

            Assert.True(context.HttpContext.Response.Headers.ContainsKey("testKey2"));
            Assert.True(context.HttpContext.Response.Headers.ContainsKey("testKey1"));
            Assert.Equal(2, context.HttpContext.Response.Headers.Count);
            Assert.True(context.HttpContext.Response.Headers.TryGetValue("testKey1", out StringValues testKey1));
            Assert.True(context.HttpContext.Response.Headers.TryGetValue("testKey2", out StringValues testKey2));
            Assert.Equal(new StringValues("3"), testKey1);
            Assert.Equal(new StringValues("2"), testKey2);
        }

        [Fact]
        public void GivenAnIgnixaNodeBackedResourceElement_WhenGettingResultToSerialize_ThenTheUnderlyingNodeIsReturned()
        {
            var patient = Samples.GetDefaultPatient().ToPoco<Patient>();

            var serializer = new IgnixaJsonSerializer();
            var schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
            var ignixaElement = new IgnixaResourceElement(serializer.Parse(patient.ToJson()), schemaContext.Schema);
            ResourceElement ignixaBackedResource = ignixaElement.ToResourceElement();

            Assert.NotNull(ignixaBackedResource.GetIgnixaNode());

            var fhirResult = new FhirResult(ignixaBackedResource);

            var resultToSerialize = GetResultToSerialize(fhirResult);

            var node = Assert.IsType<ResourceJsonNode>(resultToSerialize);
            Assert.Same(ignixaBackedResource.GetIgnixaNode(), node);
        }

        [Fact]
        public void GivenAnIgnixaRawBundleBackedResourceElement_WhenGettingResultToSerialize_ThenTheRawBundleIsReturned()
        {
            var schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
            var skeleton = new BundleJsonNode
            {
                Id = "bundle-example",
                Type = BundleJsonNode.BundleType.Searchset,
                Total = 0,
            };

            var rawBundle = new IgnixaRawBundle(skeleton, new IgnixaRawBundleEntry[0]);
            var ignixaElement = new IgnixaResourceElement(skeleton, schemaContext.Schema);
            var bundleBackedResource = new ResourceElement(ignixaElement.ToTypedElement(), rawBundle);

            Assert.NotNull(bundleBackedResource.GetIgnixaRawBundle());
            Assert.Null(bundleBackedResource.GetIgnixaNode());

            var fhirResult = new FhirResult(bundleBackedResource);

            var resultToSerialize = GetResultToSerialize(fhirResult);

            var returnedBundle = Assert.IsType<IgnixaRawBundle>(resultToSerialize);
            Assert.Same(rawBundle, returnedBundle);
        }

        [Fact]
        public void GivenAFirelyBackedResourceElement_WhenGettingResultToSerialize_ThenAPocoIsReturned()
        {
            ResourceElement firelyBackedResource = Samples.GetDefaultPatient();

            Assert.Null(firelyBackedResource.GetIgnixaNode());

            var fhirResult = new FhirResult(firelyBackedResource);

            var resultToSerialize = GetResultToSerialize(fhirResult);

            var poco = Assert.IsType<Patient>(resultToSerialize);
            Assert.Equal(firelyBackedResource.Id, poco.Id);
        }

        [Fact]
        public void GivenARawResourceElement_WhenGettingResultToSerialize_ThenTheRawResourceElementIsReturnedUnchanged()
        {
            var patient = Samples.GetDefaultPatient().ToPoco<Patient>();
            patient.Id = "example";
            patient.VersionId = "1";

            var wrapper = new ResourceWrapper(
                patient.ToResourceElement(),
                new RawResource(patient.ToJson(), FhirResourceFormat.Json, isMetaSet: false),
                null,
                false,
                null,
                null,
                null);
            var rawResourceElement = new RawResourceElement(wrapper);

            var fhirResult = new FhirResult(rawResourceElement);

            var resultToSerialize = GetResultToSerialize(fhirResult);

            Assert.Same(rawResourceElement, resultToSerialize);
        }

        private static object GetResultToSerialize(FhirResult fhirResult)
        {
            var methodInfo = typeof(FhirResult).GetMethod(
                "GetResultToSerialize",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return methodInfo.Invoke(fhirResult, System.Array.Empty<object>());
        }
    }
}
