// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.ML.Tokenizers;
using OpenAI;
using OpenAI.Embeddings;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Microsoft.Health.Fhir.Azure.VectorSearch
{
    /// <summary>
    /// Generates vector-search embeddings through an Azure AI Foundry deployment.
    /// </summary>
    public class AzureAIFoundryEmbeddingClient : IEmbeddingClient
    {
        private const int MaxBatchInputCount = 2048;
        private const int MaxBatchTokenCount = 300000;
        private const int MaxModelInputTokenCount = 8192;
        private const int RetryCount = 3;
        private const int ConsecutiveFailuresBeforeBreaking = 5;
        private static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TightBudgetTimeout = TimeSpan.FromSeconds(5);
        private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

        private readonly int _effectiveInputTokenBudget;
        private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> _generateBatchAsync;
        private readonly AsyncRetryPolicy<IReadOnlyList<float[]>> _retryPolicy;
        private readonly AsyncCircuitBreakerPolicy<IReadOnlyList<float[]>> _circuitBreakerPolicy;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureAIFoundryEmbeddingClient"/> class.
        /// </summary>
        /// <param name="configuration">The vector-search configuration.</param>
        public AzureAIFoundryEmbeddingClient(IOptions<VectorSearchConfiguration> configuration)
            : this(GetConfiguration(configuration), MaxModelInputTokenCount, generateBatchAsync: null, GetRetryDelay)
        {
        }

        internal AzureAIFoundryEmbeddingClient(
            VectorSearchConfiguration configuration,
            int configuredMaxInputTokens,
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> generateBatchAsync,
            Func<int, TimeSpan> retryDelayProvider)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(retryDelayProvider);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredMaxInputTokens);

            _effectiveInputTokenBudget = Math.Min(configuredMaxInputTokens, MaxModelInputTokenCount);
            _generateBatchAsync = generateBatchAsync ?? CreateSdkBatchGenerator(configuration);
            _retryPolicy = Policy<IReadOnlyList<float[]>>
                .Handle<ClientResultException>(IsTransient)
                .Or<TimeoutException>()
                .WaitAndRetryAsync(RetryCount, retryDelayProvider);
            _circuitBreakerPolicy = Policy<IReadOnlyList<float[]>>
                .Handle<ClientResultException>(IsTransient)
                .Or<TimeoutException>()
                .CircuitBreakerAsync(ConsecutiveFailuresBeforeBreaking, CircuitBreakDuration);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs,
            bool tightBudget,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inputs);

            if (inputs.Count == 0)
            {
                return Array.Empty<float[]>();
            }

            var results = new List<float[]>(inputs.Count);
            var batch = new List<string>(Math.Min(inputs.Count, MaxBatchInputCount));
            int batchTokenCount = 0;

            foreach (string input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string truncatedInput, int tokenCount) = Truncate(input);

                if (batch.Count == MaxBatchInputCount || batchTokenCount + tokenCount > MaxBatchTokenCount)
                {
                    await AddBatchResultsAsync(batch, results, tightBudget, cancellationToken);
                    batch = new List<string>(Math.Min(inputs.Count - results.Count, MaxBatchInputCount));
                    batchTokenCount = 0;
                }

                batch.Add(truncatedInput);
                batchTokenCount += tokenCount;
            }

            await AddBatchResultsAsync(batch, results, tightBudget, cancellationToken);
            return results;
        }

        private static VectorSearchConfiguration GetConfiguration(IOptions<VectorSearchConfiguration> configuration)
        {
            if (configuration?.Value == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            return configuration.Value;
        }

        private static Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> CreateSdkBatchGenerator(
            VectorSearchConfiguration configuration)
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri($"{configuration.EmbeddingEndpoint.AbsoluteUri.TrimEnd('/')}/openai/v1/"),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            };

#pragma warning disable OPENAI001 // BearerTokenPolicy(TokenCredential, scope) is the documented Azure authentication path.
            var authenticationPolicy = new BearerTokenPolicy(
                new DefaultAzureCredential(),
                "https://ai.azure.com/.default");
            var client = new EmbeddingClient(
                configuration.EmbeddingDeploymentName,
                authenticationPolicy,
                options);
#pragma warning restore OPENAI001
            var generationOptions = new EmbeddingGenerationOptions
            {
                Dimensions = configuration.EmbeddingDimensions,
            };

            return async (inputs, cancellationToken) =>
            {
                OpenAIEmbeddingCollection embeddings = await client.GenerateEmbeddingsAsync(
                    inputs,
                    generationOptions,
                    cancellationToken);

                return embeddings
                    .OrderBy(embedding => embedding.Index)
                    .Select(embedding => embedding.ToFloats().ToArray())
                    .ToArray();
            };
        }

        private static bool IsTransient(ClientResultException exception)
        {
            return exception.Status == 408 ||
                exception.Status == 429 ||
                exception.Status >= 500;
        }

        private static TimeSpan GetRetryDelay(int retryAttempt)
        {
            double exponentialDelayMilliseconds = 200 * Math.Pow(2, retryAttempt - 1);
            return TimeSpan.FromMilliseconds(exponentialDelayMilliseconds + RandomNumberGenerator.GetInt32(0, 100));
        }

        private async Task AddBatchResultsAsync(
            List<string> batch,
            List<float[]> results,
            bool tightBudget,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<float[]> batchResults = await ExecuteBatchAsync(batch, tightBudget, cancellationToken);
            if (batchResults.Count != batch.Count)
            {
                throw new EmbeddingUnavailableException();
            }

            results.AddRange(batchResults);
        }

        private async Task<IReadOnlyList<float[]>> ExecuteBatchAsync(
            IReadOnlyList<string> batch,
            bool tightBudget,
            CancellationToken cancellationToken)
        {
            try
            {
                if (tightBudget)
                {
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutSource.CancelAfter(TightBudgetTimeout);
                    return await GenerateBatchWithTimeoutClassificationAsync(batch, timeoutSource.Token);
                }

                return await _circuitBreakerPolicy.ExecuteAsync(
                    token => _retryPolicy.ExecuteAsync(
                        innerToken => GenerateBatchWithTimeoutClassificationAsync(batch, innerToken),
                        token),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EmbeddingUnavailableException();
            }
            catch (BrokenCircuitException)
            {
                throw new EmbeddingUnavailableException();
            }
            catch (ClientResultException)
            {
                throw new EmbeddingUnavailableException();
            }
            catch (TimeoutException)
            {
                throw new EmbeddingUnavailableException();
            }
        }

        private async Task<IReadOnlyList<float[]>> GenerateBatchWithTimeoutClassificationAsync(
            IReadOnlyList<string> batch,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _generateBatchAsync(batch, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }

        private (string Input, int TokenCount) Truncate(string input)
        {
            int endIndex = Tokenizer.GetIndexByTokenCount(
                input,
                _effectiveInputTokenBudget,
                out string normalizedText,
                out int tokenCount);
            string textToSlice = normalizedText ?? input;
            return (textToSlice[..endIndex], tokenCount);
        }
    }
}
