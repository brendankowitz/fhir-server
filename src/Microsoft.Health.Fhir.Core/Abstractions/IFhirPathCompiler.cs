// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Hl7.Fhir.ElementModel;
using Hl7.FhirPath;

namespace Microsoft.Health.Fhir.Core.Abstractions
{
    /// <summary>
    /// Provides an abstraction over FhirPath expression compilation and execution.
    /// This interface allows switching between different FhirPath engine implementations
    /// (e.g., Firely SDK, Ignixa SDK) without changing the consuming code.
    /// </summary>
    public interface IFhirPathCompiler
    {
        /// <summary>
        /// Compiles a FhirPath expression string into an executable form.
        /// </summary>
        /// <param name="expression">The FhirPath expression to compile.</param>
        /// <returns>A compiled expression that can be invoked multiple times.</returns>
        ICompiledExpression Compile(string expression);
    }

    /// <summary>
    /// Represents a compiled FhirPath expression that can be evaluated against FHIR resources.
    /// </summary>
    public interface ICompiledExpression
    {
        /// <summary>
        /// Invokes the compiled expression against a typed element with an evaluation context.
        /// </summary>
        /// <param name="element">The typed element to evaluate the expression against.</param>
        /// <param name="context">The evaluation context containing resolver functions and variables.</param>
        /// <returns>An enumerable of typed elements matching the expression.</returns>
        IEnumerable<ITypedElement> Invoke(ITypedElement element, EvaluationContext context);
    }
}
