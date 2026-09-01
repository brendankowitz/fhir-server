// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.VectorSearch
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SuspendableEmbeddingClientTests
    {
        [Fact]
        public async Task GivenSuspendedConfig_EmbedAsyncThrowsEmbeddingUnavailable_WithoutCallingInner()
        {
            var inner = Substitute.For<IEmbeddingClient>();
            var config = new VectorSearchConfiguration { Enabled = true, Suspended = true };
            var client = new SuspendableEmbeddingClient(inner, Options.Create(new CoreFeatureConfiguration { VectorSearch = config }));

            await Assert.ThrowsAsync<EmbeddingUnavailableException>(
                () => client.EmbedAsync(new[] { "x" }, tightBudget: false, CancellationToken.None));
            await inner.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenUnsuspendedConfig_EmbedAsyncDelegatesToInnerAndReturnsResult()
        {
            var expected = (IReadOnlyList<float[]>)new[] { new[] { 1.0f, 2.0f } };
            var inner = Substitute.For<IEmbeddingClient>();
            inner.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(expected));
            var config = new VectorSearchConfiguration { Enabled = true, Suspended = false };
            var client = new SuspendableEmbeddingClient(inner, Options.Create(new CoreFeatureConfiguration { VectorSearch = config }));

            var actual = await client.EmbedAsync(new[] { "x" }, tightBudget: true, CancellationToken.None);

            Assert.Same(expected, actual);
            await inner.Received(1).EmbedAsync(Arg.Is<IReadOnlyList<string>>(inputs => inputs[0] == "x"), true, CancellationToken.None);
        }
    }
}
