// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics.CodeAnalysis;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Search.Expressions
{
    public class VectorSearchExpression : Expression
    {
        public VectorSearchExpression(SearchParameterInfo parameter, string queryText, int? k, double? minScore)
        {
            Parameter = EnsureArg.IsNotNull(parameter, nameof(parameter));
            QueryText = EnsureArg.IsNotNullOrWhiteSpace(queryText, nameof(queryText));
            K = k;
            MinScore = minScore;
        }

        public SearchParameterInfo Parameter { get; }

        public string QueryText { get; }

        public int? K { get; }

        public double? MinScore { get; }

        // Filled by the search service immediately before SQL generation; never serialized.
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The required expression API exposes query embeddings as float arrays.")]
        public float[] QueryEmbedding { get; private set; }

        public void SetQueryEmbedding(float[] embedding) => QueryEmbedding = EnsureArg.IsNotNull(embedding, nameof(embedding));

        public override TOutput AcceptVisitor<TContext, TOutput>(IExpressionVisitor<TContext, TOutput> visitor, TContext context)
            => visitor.VisitVectorSearch(this, context);

        public override string ToString() => $"(VectorSearch {Parameter.Code} k:{K})";

        public override void AddValueInsensitiveHashCode(ref HashCode hashCode) => hashCode.Add(Parameter.Url);

        public override bool ValueInsensitiveEquals(Expression other)
            => other is VectorSearchExpression vectorSearch && Equals(vectorSearch.Parameter.Url, Parameter.Url);
    }
}
