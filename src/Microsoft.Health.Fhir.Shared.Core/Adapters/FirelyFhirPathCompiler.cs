// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Collections.Generic;
using EnsureThat;
using Hl7.Fhir.ElementModel;
using Hl7.FhirPath;
using Microsoft.Health.Fhir.Core.Abstractions;

namespace Microsoft.Health.Fhir.Shared.Core.Adapters
{
    /// <summary>
    /// Adapter implementation of <see cref="IFhirPathCompiler"/> using the Firely SDK.
    /// This is a temporary adapter to maintain compatibility during the migration to Ignixa SDK.
    /// </summary>
    public class FirelyFhirPathCompiler : IFhirPathCompiler
    {
        private static readonly FhirPathCompiler _compiler = new();
        private readonly ConcurrentDictionary<string, ICompiledExpression> _compiledExpressions = new();

        /// <inheritdoc />
        public ICompiledExpression Compile(string expression)
        {
            EnsureArg.IsNotNullOrWhiteSpace(expression, nameof(expression));

            return _compiledExpressions.GetOrAdd(
                expression,
                expr => new FirelyCompiledExpression(_compiler.Compile(expr)));
        }

        /// <summary>
        /// Adapter wrapper for Firely's <see cref="CompiledExpression"/>.
        /// </summary>
        private class FirelyCompiledExpression : ICompiledExpression
        {
            private readonly CompiledExpression _firelyExpression;

            public FirelyCompiledExpression(CompiledExpression firelyExpression)
            {
                EnsureArg.IsNotNull(firelyExpression, nameof(firelyExpression));
                _firelyExpression = firelyExpression;
            }

            /// <inheritdoc />
            public IEnumerable<ITypedElement> Invoke(ITypedElement element, EvaluationContext context)
            {
                return _firelyExpression.Invoke(element, context);
            }
        }
    }
}
