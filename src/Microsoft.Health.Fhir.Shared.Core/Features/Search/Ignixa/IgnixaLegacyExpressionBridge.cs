// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Definition;
using FhirExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;
using IgnixaExpression = Ignixa.Search.Expressions.Expression;

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1201 // Elements should appear in the correct order

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Default implementation of <see cref="IIgnixaLegacyExpressionBridge"/>. Converts an already-lowered Ignixa
    /// expression into the FHIR Server legacy expression model using <see cref="IgnixaLegacyExpressionBridgeVisitor"/>.
    /// </summary>
    public sealed class IgnixaLegacyExpressionBridge : IIgnixaLegacyExpressionBridge
    {
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaLegacyExpressionBridge"/> class.
        /// </summary>
        /// <param name="searchParameterDefinitionManager">The FHIR Server search parameter definition manager.</param>
        public IgnixaLegacyExpressionBridge(ISearchParameterDefinitionManager searchParameterDefinitionManager)
        {
            EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));

            _searchParameterDefinitionManager = searchParameterDefinitionManager;
        }

        /// <inheritdoc />
        public FhirExpression Convert(IgnixaExpression loweredExpression)
        {
            EnsureArg.IsNotNull(loweredExpression, nameof(loweredExpression));

            var visitor = new IgnixaLegacyExpressionBridgeVisitor(_searchParameterDefinitionManager);
            return loweredExpression.AcceptVisitor(visitor, context: null);
        }
    }

    /// <summary>
    /// Represents a structured failure raised when an Ignixa legacy expression node or value cannot be represented in
    /// the FHIR Server Cosmos expression model. Deriving from <see cref="SearchOperationNotSupportedException"/>
    /// ensures the failure is surfaced through the existing search-not-supported contract before any Cosmos query
    /// executes, rather than silently returning an empty or broadened expression.
    /// </summary>
    internal sealed class IgnixaExpressionBridgeException : SearchOperationNotSupportedException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaExpressionBridgeException"/> class.
        /// </summary>
        /// <param name="nodeType">The Ignixa expression node type that could not be bridged.</param>
        /// <param name="parameterCode">The originating search parameter code, when available.</param>
        /// <param name="reason">A human-readable description of why the node could not be bridged.</param>
        internal IgnixaExpressionBridgeException(string nodeType, string parameterCode, string reason)
            : base($"Ignixa expression node '{nodeType}' for parameter '{parameterCode ?? "(none)"}' cannot be lowered for Cosmos: {reason}")
        {
            NodeType = nodeType;
            ParameterCode = parameterCode;
            Reason = reason;
        }

        /// <summary>
        /// Gets the Ignixa expression node type that could not be bridged.
        /// </summary>
        internal string NodeType { get; }

        /// <summary>
        /// Gets the originating search parameter code, when available.
        /// </summary>
        internal string ParameterCode { get; }

        /// <summary>
        /// Gets the reason the node could not be bridged.
        /// </summary>
        internal string Reason { get; }
    }
}
