// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Azure.VectorSearch;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Azure.UnitTests.VectorSearch
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Operations)]
    public class AzureAIFoundryEmbeddingClientTests
    {
        private static readonly Func<int, TimeSpan> NoRetryDelay = _ => TimeSpan.Zero;

        [Fact]
        public async Task GivenTransientFailure_WhenEmbedding_ThenRetriesAndReturnsResult()
        {
            // Arrange
            int attempts = 0;
            var expected = new[] { new[] { 1.0f } };
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (_, _) =>
                {
                    attempts++;
                    return attempts == 1
                        ? Task.FromException<IReadOnlyList<float[]>>(new TimeoutException())
                        : Task.FromResult<IReadOnlyList<float[]>>(expected);
                });

            // Act
            IReadOnlyList<float[]> result = await client.EmbedAsync(new[] { "input" }, tightBudget: false, CancellationToken.None);

            // Assert
            Assert.Same(expected[0], Assert.Single(result));
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task GivenPersistentTransientFailure_WhenEmbedding_ThenThrowsEmbeddingUnavailableExceptionAfterRetries()
        {
            // Arrange
            int attempts = 0;
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (_, _) =>
                {
                    attempts++;
                    return Task.FromException<IReadOnlyList<float[]>>(new TimeoutException());
                });

            // Act
            Task Act() => client.EmbedAsync(new[] { "input" }, tightBudget: false, CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<EmbeddingUnavailableException>(Act);
            Assert.Equal(4, attempts);
        }

        [Fact]
        public async Task GivenFiveConsecutiveFailedExecutions_WhenEmbeddingAgain_ThenCircuitRejectsWithoutCallingService()
        {
            // Arrange
            int attempts = 0;
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (_, _) =>
                {
                    attempts++;
                    return Task.FromException<IReadOnlyList<float[]>>(new TimeoutException());
                });

            for (int failure = 0; failure < 5; failure++)
            {
                await Assert.ThrowsAsync<EmbeddingUnavailableException>(
                    () => client.EmbedAsync(new[] { "input" }, tightBudget: false, CancellationToken.None));
            }

            int attemptsBeforeOpenCircuitCall = attempts;

            // Act
            Task Act() => client.EmbedAsync(new[] { "input" }, tightBudget: false, CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<EmbeddingUnavailableException>(Act);
            Assert.Equal(20, attemptsBeforeOpenCircuitCall);
            Assert.Equal(attemptsBeforeOpenCircuitCall, attempts);
        }

        [Fact]
        public async Task GivenTightBudget_WhenServiceFails_ThenPerformsExactlyOneAttempt()
        {
            // Arrange
            int attempts = 0;
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (_, _) =>
                {
                    attempts++;
                    return Task.FromException<IReadOnlyList<float[]>>(new TimeoutException());
                });

            // Act
            Task Act() => client.EmbedAsync(new[] { "input" }, tightBudget: true, CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<EmbeddingUnavailableException>(Act);
            Assert.Equal(1, attempts);
        }

        [Theory]
        [InlineData(3, 4, 3)]
        [InlineData(10000, 8193, 8192)]
        public async Task GivenInputOverEffectiveTokenBudget_WhenEmbedding_ThenTruncatesAtConfiguredBudgetClampedToModelLimit(
            int configuredMaxInputTokens,
            int inputTokenCount,
            int expectedTokenCount)
        {
            // Arrange
            string input = RepeatToken("hello", inputTokenCount);
            string expected = RepeatToken("hello", expectedTokenCount);
            string actual = null;
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (batch, _) =>
                {
                    actual = Assert.Single(batch);
                    return Task.FromResult<IReadOnlyList<float[]>>(new[] { new[] { 1.0f } });
                },
                configuredMaxInputTokens);

            // Act
            await client.EmbedAsync(new[] { input }, tightBudget: true, CancellationToken.None);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task GivenMoreThanMaximumInputs_WhenEmbedding_ThenSplitsBatchesAtInputLimit()
        {
            // Arrange
            var batchSizes = new List<int>();
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (batch, _) =>
                {
                    batchSizes.Add(batch.Count);
                    return Task.FromResult<IReadOnlyList<float[]>>(batch.Select(_ => new[] { 1.0f }).ToArray());
                });
            string[] inputs = Enumerable.Repeat("hello", 2049).ToArray();

            // Act
            IReadOnlyList<float[]> result = await client.EmbedAsync(inputs, tightBudget: true, CancellationToken.None);

            // Assert
            Assert.Equal(new[] { 2048, 1 }, batchSizes);
            Assert.Equal(inputs.Length, result.Count);
        }

        [Fact]
        public async Task GivenAggregateTokensOverMaximum_WhenEmbedding_ThenSplitsBatchesAtTokenLimit()
        {
            // Arrange
            var batchSizes = new List<int>();
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (batch, _) =>
                {
                    batchSizes.Add(batch.Count);
                    return Task.FromResult<IReadOnlyList<float[]>>(batch.Select(_ => new[] { 1.0f }).ToArray());
                });
            string input = RepeatToken("hello", 151);
            string[] inputs = Enumerable.Repeat(input, 1987).ToArray();

            // Act
            IReadOnlyList<float[]> result = await client.EmbedAsync(inputs, tightBudget: true, CancellationToken.None);

            // Assert
            Assert.Equal(new[] { 1986, 1 }, batchSizes);
            Assert.Equal(inputs.Length, result.Count);
        }

        [Fact]
        public async Task GivenMultipleBatches_WhenEmbedding_ThenResultsRemainInOriginalInputOrder()
        {
            // Arrange
            AzureAIFoundryEmbeddingClient client = CreateClient(
                (batch, _) =>
                    Task.FromResult<IReadOnlyList<float[]>>(
                        batch.Select(value => new[] { float.Parse(value, CultureInfo.InvariantCulture) }).ToArray()));
            string[] inputs = Enumerable.Range(0, 2050).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();

            // Act
            IReadOnlyList<float[]> result = await client.EmbedAsync(inputs, tightBudget: true, CancellationToken.None);

            // Assert
            Assert.Equal(inputs.Length, result.Count);
            Assert.Equal(Enumerable.Range(0, inputs.Length).Select(value => (float)value), result.Select(vector => Assert.Single(vector)));
        }

        private static AzureAIFoundryEmbeddingClient CreateClient(
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> generateBatchAsync,
            int configuredMaxInputTokens = 8192)
        {
            var configuration = new VectorSearchConfiguration
            {
                EmbeddingEndpoint = new Uri("https://example.test"),
                EmbeddingDeploymentName = "deployment",
                EmbeddingDimensions = 1,
            };

            return new AzureAIFoundryEmbeddingClient(
                configuration,
                configuredMaxInputTokens,
                generateBatchAsync,
                NoRetryDelay);
        }

        private static string RepeatToken(string token, int count)
        {
            return string.Join(' ', Enumerable.Repeat(token, count));
        }
    }
}
