// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Extensions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.ConditionalOperations)]
    public class PreferHeaderExtensionsTests
    {
        [Theory]
        [InlineData("handling=strict", SearchParameterHandling.Strict, false)]
        [InlineData("handling=lenient", SearchParameterHandling.Lenient, false)]
        [InlineData("handling=Lenient", SearchParameterHandling.Lenient, false)]
        [InlineData("handling=unknown", null, true)]
        [InlineData("handlings=strict", null, false)]
        [InlineData("abcdefg", null, false)]
        public void GivenARequestContextAccessor_WhenCheckingHandlingrHeader_ThenSearchParameterHandlingShouldBeReadSuccessfully(
            string preferHeaderValue,
            SearchParameterHandling? expected,
            bool throwException)
        {
            var headers = new Dictionary<string, StringValues>();
            if (!string.IsNullOrEmpty(preferHeaderValue))
            {
                headers.Add(KnownHeaders.Prefer, preferHeaderValue);
            }

            var fhirRequestContext = Substitute.For<IFhirRequestContext>();
            fhirRequestContext.RequestHeaders.Returns(headers);

            var requestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            requestContextAccessor.RequestContext.Returns(fhirRequestContext);

            try
            {
                var actual = requestContextAccessor.GetHandlingHeader();
                Assert.Equal(actual, expected);
                Assert.False(throwException);
            }
            catch (BadRequestException)
            {
                Assert.True(throwException);
            }
        }

        [Theory]
        [InlineData("return=minimal", ReturnPreference.Minimal, false)]
        [InlineData("return=representation", ReturnPreference.Representation, false)]
        [InlineData("return=OperationOutcome", ReturnPreference.OperationOutcome, false)]
        [InlineData("return=operatioNoutcomE", ReturnPreference.OperationOutcome, false)]
        [InlineData("return=unknown", null, true)]
        [InlineData("returns=minimal", null, false)]
        [InlineData("abcdefg", null, false)]
        public void GivenARequestContextAccessor_WhenCheckingPreferHeader_ThenReturnPreferenceShouldBeReadSuccessfully(
            string preferHeaderValue,
            ReturnPreference? expected,
            bool throwException)
        {
            var headers = new Dictionary<string, StringValues>();
            if (!string.IsNullOrEmpty(preferHeaderValue))
            {
                headers.Add(KnownHeaders.Prefer, preferHeaderValue);
            }

            var fhirRequestContext = Substitute.For<IFhirRequestContext>();
            fhirRequestContext.RequestHeaders.Returns(headers);

            var requestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            requestContextAccessor.RequestContext.Returns(fhirRequestContext);

            try
            {
                var actual = requestContextAccessor.GetReturnPreferenceValue();
                Assert.Equal(actual, expected);
                Assert.False(throwException);
            }
            catch (BadRequestException)
            {
                Assert.True(throwException);
            }
        }

        [Theory]
        [InlineData("embedding-persistence=synchronous")]
        [InlineData("embedding-persistence=Synchronous")]
        public void GivenSynchronousEmbeddingPreference_WhenCheckingHeader_ThenReturnsTrue(string preferHeaderValue)
        {
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor = CreateAccessor(preferHeaderValue);

            Assert.True(requestContextAccessor.GetPrefersSynchronousEmbedding());
        }

        [Theory]
        [InlineData("embedding-persistence=asynchronous")]
        [InlineData("embedding-persistence=banana")]
        [InlineData("embedding-persistence=")]
        public void GivenUnhonoredEmbeddingPreference_WhenCheckingHeader_ThenReturnsFalseWithoutThrowing(string preferHeaderValue)
        {
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor = CreateAccessor(preferHeaderValue);

            Assert.False(requestContextAccessor.GetPrefersSynchronousEmbedding());
        }

        [Fact]
        public void GivenNoPreferHeader_WhenCheckingSynchronousEmbeddingPreference_ThenReturnsFalse()
        {
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor = CreateAccessor(null);

            Assert.False(requestContextAccessor.GetPrefersSynchronousEmbedding());
        }

        [Fact]
        public void GivenNoRequestContext_WhenCheckingSynchronousEmbeddingPreference_ThenReturnsFalse()
        {
            var requestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            requestContextAccessor.RequestContext.Returns((IFhirRequestContext)null);

            Assert.False(requestContextAccessor.GetPrefersSynchronousEmbedding());
        }

        [Fact]
        public void GivenCoexistingPreferTokens_WhenCheckingHeader_ThenAllPreferencesAreRead()
        {
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor = CreateAccessor(
                "return=representation, handling=strict, embedding-persistence=synchronous");

            Assert.True(requestContextAccessor.GetPrefersSynchronousEmbedding());
            Assert.Equal(ReturnPreference.Representation, requestContextAccessor.GetReturnPreferenceValue());
            Assert.Equal(SearchParameterHandling.Strict, requestContextAccessor.GetHandlingHeader());
        }

        [Fact]
        public void GivenOnlyUnknownEmbeddingPreference_WhenCheckingOtherPreferences_ThenTheyRemainAbsentWithoutThrowing()
        {
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor = CreateAccessor("embedding-persistence=banana");

            Assert.Null(requestContextAccessor.GetReturnPreferenceValue());
            Assert.Null(requestContextAccessor.GetHandlingHeader());
        }

        private static RequestContextAccessor<IFhirRequestContext> CreateAccessor(string preferHeaderValue)
        {
            var headers = new Dictionary<string, StringValues>();
            if (preferHeaderValue != null)
            {
                headers.Add(KnownHeaders.Prefer, preferHeaderValue);
            }

            var fhirRequestContext = Substitute.For<IFhirRequestContext>();
            fhirRequestContext.RequestHeaders.Returns(headers);

            var requestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            requestContextAccessor.RequestContext.Returns(fhirRequestContext);
            return requestContextAccessor;
        }
    }
}
