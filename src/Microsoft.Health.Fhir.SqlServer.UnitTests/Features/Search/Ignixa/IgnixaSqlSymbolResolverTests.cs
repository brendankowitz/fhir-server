// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Ignixa.Search.Sql.Symbols;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaSqlSymbolResolverTests
    {
        private readonly ISqlServerFhirModel _model;
        private readonly ISymbolResolver _resolver;

        public IgnixaSqlSymbolResolverTests()
        {
            _model = Substitute.For<ISqlServerFhirModel>();
            _resolver = new IgnixaSqlSymbolResolver(_model);
        }

        [Fact]
        public async Task GetResourceTypeIdAsync_WhenResourceTypeExists_ReturnsId()
        {
            // Arrange
            const string resourceType = "Patient";
            const short expectedId = 42;
            _model.TryGetResourceTypeId(resourceType, out Arg.Any<short>())
                .Returns(x =>
                {
                    x[1] = expectedId;
                    return true;
                });

            // Act
            short? result = await _resolver.GetResourceTypeIdAsync(resourceType, CancellationToken.None);

            // Assert
            Assert.Equal(expectedId, result);
        }

        [Fact]
        public async Task GetResourceTypeIdAsync_WhenResourceTypeMissing_ReturnsNull()
        {
            // Arrange
            _model.TryGetResourceTypeId("Unknown", out Arg.Any<short>())
                .Returns(false);

            // Act
            short? result = await _resolver.GetResourceTypeIdAsync("Unknown", CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetSearchParamIdAsync_WhenParameterExists_ReturnsId()
        {
            // Arrange
            var url = new Uri("http://hl7.org/fhir/SearchParameter/Patient-name");
            const short expectedId = 7;
            _model.TryGetSearchParamId(url, out Arg.Any<short>())
                .Returns(x =>
                {
                    x[1] = expectedId;
                    return true;
                });

            var parameter = CreateParameter("name", url);

            // Act
            short? result = await _resolver.GetSearchParamIdAsync(parameter, CancellationToken.None);

            // Assert
            Assert.Equal(expectedId, result);
        }

        [Fact]
        public async Task GetSearchParamIdAsync_WhenParameterMissing_ReturnsNull()
        {
            // Arrange
            var url = new Uri("http://hl7.org/fhir/SearchParameter/Patient-unknown");
            _model.TryGetSearchParamId(url, out Arg.Any<short>())
                .Returns(false);

            var parameter = CreateParameter("unknown", url);

            // Act
            short? result = await _resolver.GetSearchParamIdAsync(parameter, CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetSearchParamIdAsync_WhenUrlIsNull_ReturnsNull()
        {
            // Arrange - use 2-arg ctor which leaves Url as null by default
            var parameter = new IgnixaSearchParameterInfo("test", "test");

            // Act
            short? result = await _resolver.GetSearchParamIdAsync(parameter, CancellationToken.None);

            // Assert
            Assert.Null(result);
            _model.DidNotReceiveWithAnyArgs().TryGetSearchParamId(default!, out Arg.Any<short>());
        }

        [Fact]
        public async Task GetResourceTypeIdAsync_WhenCancelled_ThrowsOperationCanceledException()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _resolver.GetResourceTypeIdAsync("Patient", cts.Token));
        }

        [Fact]
        public async Task GetSearchParamIdAsync_WhenCancelled_ThrowsOperationCanceledException()
        {
            // Arrange
            var parameter = CreateParameter("name", new Uri("http://example.org/sp"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _resolver.GetSearchParamIdAsync(parameter, cts.Token));
        }

        [Fact]
        public async Task GetResourceTypeIdAsync_WhenModelThrows_PropagatesException()
        {
            // Arrange
            var expectedException = new InvalidOperationException("Model not initialized");
            _model.TryGetResourceTypeId(Arg.Any<string>(), out Arg.Any<short>())
                .Throws(expectedException);

            // Act & Assert
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _resolver.GetResourceTypeIdAsync("Patient", CancellationToken.None));
            Assert.Same(expectedException, thrown);
        }

        [Fact]
        public async Task GetSearchParamIdAsync_WhenModelThrows_PropagatesException()
        {
            // Arrange
            var url = new Uri("http://hl7.org/fhir/SearchParameter/Patient-name");
            var expectedException = new InvalidOperationException("Model not initialized");
            _model.TryGetSearchParamId(Arg.Any<Uri>(), out Arg.Any<short>())
                .Throws(expectedException);

            var parameter = CreateParameter("name", url);

            // Act & Assert
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _resolver.GetSearchParamIdAsync(parameter, CancellationToken.None));
            Assert.Same(expectedException, thrown);
        }

        [Fact]
        public async Task GetSearchParamIdAsync_DoesNotUseOverridesUrl()
        {
            // Arrange: parameter has OverridesUrl set but Url is the canonical lookup key
            var canonicalUrl = new Uri("http://hl7.org/fhir/SearchParameter/Patient-name");
            var overrideUrl = new Uri("http://custom.org/fhir/SearchParameter/Patient-name-override");
            const short expectedId = 10;

            _model.TryGetSearchParamId(canonicalUrl, out Arg.Any<short>())
                .Returns(x =>
                {
                    x[1] = expectedId;
                    return true;
                });
            _model.TryGetSearchParamId(overrideUrl, out Arg.Any<short>())
                .Returns(false);

            var parameter = CreateParameter("name", canonicalUrl);
            parameter.OverridesUrl = overrideUrl;

            // Act
            short? result = await _resolver.GetSearchParamIdAsync(parameter, CancellationToken.None);

            // Assert: resolves by canonical Url, not OverridesUrl
            Assert.Equal(expectedId, result);
            _model.Received(1).TryGetSearchParamId(canonicalUrl, out Arg.Any<short>());
            _model.DidNotReceive().TryGetSearchParamId(overrideUrl, out Arg.Any<short>());
        }

        /// <summary>
        /// Helper that creates an <see cref="IgnixaSearchParameterInfo"/> using the 2-arg constructor
        /// and reflection to set the Url property (which is init-only via the full constructor).
        /// </summary>
        private static IgnixaSearchParameterInfo CreateParameter(string name, Uri url)
        {
            // Use the (name, code) constructor and set Url via reflection since the
            // full constructor requires Ignixa.Specification.ValueSets.Normative.SearchParamType.
            var parameter = new IgnixaSearchParameterInfo(name, name);

            // Url is a get-only property backed by a field; set it via the backing field.
            var urlField = typeof(IgnixaSearchParameterInfo).GetField(
                "<Url>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            urlField?.SetValue(parameter, url);

            return parameter;
        }
    }
}
