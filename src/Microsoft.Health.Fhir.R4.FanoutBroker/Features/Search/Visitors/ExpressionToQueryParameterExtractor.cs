// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
    /// <summary>
    /// Visitor to extract query parameters from search expressions for reconstruction into FHIR search URLs.
    /// This enables the fanout broker to determine chained searches and includes from the Expression inside SearchOptions
    /// and reconstruct the appropriate FHIR search queries to send to sub-servers.
    /// </summary>
    internal class ExpressionToQueryParameterExtractor : DefaultExpressionVisitor<object, object>
    {
        private readonly List<Tuple<string, string>> _queryParameters = new();
        private readonly HashSet<string> _resourceTypes = new();
        private readonly HashSet<string> _addedParameters = new();
        private readonly string _contextResourceType;
        private bool _typeParameterAdded = false;

        public ExpressionToQueryParameterExtractor(string contextResourceType = null)
        {
            _contextResourceType = contextResourceType;
        }

        /// <summary>
        /// Gets the extracted query parameters from the expression tree.
        /// </summary>
        public IReadOnlyList<Tuple<string, string>> QueryParameters
        {
            get
            {
                var parameters = new List<Tuple<string, string>>(_queryParameters);

                // Only add _type parameter if:
                // 1. We have multiple resource types in a multi-type search AND
                // 2. No context resource type is specified (i.e., not a resource-specific endpoint)
                if (_resourceTypes.Count > 1 && string.IsNullOrEmpty(_contextResourceType) && !_typeParameterAdded)
                {
                    parameters.Insert(0, Tuple.Create("_type", string.Join(",", _resourceTypes.OrderBy(t => t))));
                    _typeParameterAdded = true;
                }

                return parameters.AsReadOnly();
            }
        }

        public override object VisitSearchParameter(SearchParameterExpression expression, object context)
        {
            // Extract the parameter name and convert the expression to a query value
            var parameterName = expression.Parameter.Code;
            var parameterValue = ConvertExpressionToQueryValue(expression.Expression);

            // Special handling for reference parameters that might have been split into components
            if (expression.Parameter.Type == SearchParamType.Reference && expression.Expression is MultiaryExpression multiaryExpr && multiaryExpr.MultiaryOperation == MultiaryOperator.And)
            {
                // Try to reconstruct reference from ReferenceResourceType and ReferenceResourceId
                parameterValue = ReconstructReferenceValue(multiaryExpr);
            }

            // Check for special expression types that require parameter name modifiers
            if (expression.Expression is NotExpression notExpr)
            {
                // For Not expressions, the modifier goes in the parameter name, not the value
                parameterName += ":not";
                parameterValue = ConvertExpressionToQueryValue(notExpr.Expression);
            }
            else if (expression.Expression is MissingFieldExpression)
            {
                // For Missing expressions, use the :missing modifier
                parameterName += ":missing";
                parameterValue = "true"; // Missing field is true
            }
            else if (expression.Expression is StringExpression strExpr)
            {
                // Handle string search modifiers like :exact and :contains
                // Note: Modifiers are often already included in the parameter name from parsing
                switch (strExpr.StringOperator)
                {
                    case StringOperator.Contains:
                        if (!parameterName.EndsWith(":contains", StringComparison.Ordinal))
                            parameterName += ":contains";
                        parameterValue = strExpr.Value; // Use raw value for contains
                        break;
                    case StringOperator.Equals when parameterName.EndsWith(":exact", StringComparison.Ordinal):
                        parameterValue = strExpr.Value; // Use exact value without wildcards
                        break;
                }
            }

            if (!string.IsNullOrEmpty(parameterValue))
            {
                AddParameterIfNotExists(parameterName, parameterValue);
            }

            return base.VisitSearchParameter(expression, context);
        }

        public override object VisitNotExpression(NotExpression expression, object context)
        {
            // Visit the inner expression - the Not handling is done at the SearchParameter level
            return expression.Expression.AcceptVisitor(this, context);
        }

        public override object VisitMissingField(MissingFieldExpression expression, object context)
        {
            // Missing field expressions are handled at the SearchParameter level
            return base.VisitMissingField(expression, context);
        }

        public override object VisitChained(ChainedExpression expression, object context)
        {
            // For chained searches, we only collect the source resource types
            // Target resource types are part of the chained parameter syntax, not _type parameters
            if (expression.ResourceTypes?.Length > 0)
            {
                foreach (var resourceType in expression.ResourceTypes)
                {
                    _resourceTypes.Add(resourceType);
                }
            }

            // Do NOT add target resource types to _resourceTypes collection
            // as they are part of the chained parameter syntax

            // For chained expressions, we need to handle nested SearchParameterExpression specially
            if (expression.Expression is SearchParameterExpression nestedSearchParam)
            {
                // Reconstruct chained search parameter (e.g., "subject:Patient.name")
                var chainedParamName = expression.ReferenceSearchParameter.Code;

                // Add target resource type if specified
                if (expression.TargetResourceTypes?.Length > 0)
                {
                    if (expression.TargetResourceTypes.Length == 1)
                    {
                        chainedParamName += ":" + expression.TargetResourceTypes[0];
                    }
                    else
                    {
                        chainedParamName += ":" + string.Join(",", expression.TargetResourceTypes);
                    }
                }

                // Add the nested parameter name to create full chained parameter
                chainedParamName += "." + nestedSearchParam.Parameter.Code;

                // Extract the value from the nested expression
                var chainedValue = ConvertExpressionToQueryValue(nestedSearchParam.Expression);
                if (!string.IsNullOrEmpty(chainedValue))
                {
                    AddParameterIfNotExists(chainedParamName, chainedValue);
                }
            }
            else
            {
                // Fallback for non-SearchParameterExpression cases
                var chainedParamName = expression.ReferenceSearchParameter.Code;

                // Add target resource type if specified
                if (expression.TargetResourceTypes?.Length > 0)
                {
                    if (expression.TargetResourceTypes.Length == 1)
                    {
                        chainedParamName += ":" + expression.TargetResourceTypes[0];
                    }
                    else
                    {
                        chainedParamName += ":" + string.Join(",", expression.TargetResourceTypes);
                    }
                }

                var chainedValue = ConvertExpressionToQueryValue(expression.Expression);
                if (!string.IsNullOrEmpty(chainedValue))
                {
                    AddParameterIfNotExists(chainedParamName, chainedValue);
                }
            }

            // Do NOT call base.VisitChained() to prevent double-processing of nested expressions
            return null;
        }

        public override object VisitInclude(IncludeExpression expression, object context)
        {
            // Collect resource types for _type parameter generation
            if (expression.ResourceTypes?.Length > 0)
            {
                foreach (var resourceType in expression.ResourceTypes)
                {
                    _resourceTypes.Add(resourceType);
                }
            }

            if (!string.IsNullOrEmpty(expression.SourceResourceType))
            {
                _resourceTypes.Add(expression.SourceResourceType);
            }

            if (!string.IsNullOrEmpty(expression.TargetResourceType))
            {
                _resourceTypes.Add(expression.TargetResourceType);
            }

            // Reconstruct _include or _revinclude parameters
            var includeParam = expression.Reversed ? "_revinclude" : "_include";

            var parameterBuilder = new StringBuilder();

            // Add source resource type for _include or target resource type for _revinclude
            if (expression.Reversed && !string.IsNullOrEmpty(expression.SourceResourceType))
            {
                parameterBuilder.Append(expression.SourceResourceType);
            }
            else if (!expression.Reversed && expression.ResourceTypes?.Length > 0)
            {
                // For multiple resource types, create separate include parameters
                if (expression.ResourceTypes.Length > 1)
                {
                    foreach (var resourceType in expression.ResourceTypes)
                    {
                        var singleIncludeBuilder = new StringBuilder();
                        singleIncludeBuilder.Append(resourceType);

                        // Add reference search parameter
                        if (expression.ReferenceSearchParameter != null)
                        {
                            singleIncludeBuilder.Append(':').Append(expression.ReferenceSearchParameter.Code);
                        }

                        // Add target resource type for _include
                        if (!expression.Reversed && !string.IsNullOrEmpty(expression.TargetResourceType))
                        {
                            singleIncludeBuilder.Append(':').Append(expression.TargetResourceType);
                        }

                        // Add :iterate modifier if present
                        if (expression.Iterate)
                        {
                            singleIncludeBuilder.Append(":iterate");
                        }

                        var singleIncludeValue = singleIncludeBuilder.ToString();
                        if (!string.IsNullOrEmpty(singleIncludeValue))
                        {
                            AddParameterIfNotExists(includeParam, singleIncludeValue);
                        }
                    }

                    return base.VisitInclude(expression, context);
                }
                else
                {
                    parameterBuilder.Append(expression.ResourceTypes[0]);
                }
            }

            // Add reference search parameter
            if (expression.ReferenceSearchParameter != null)
            {
                parameterBuilder.Append(':').Append(expression.ReferenceSearchParameter.Code);
            }

            // Add target resource type for _include
            if (!expression.Reversed && !string.IsNullOrEmpty(expression.TargetResourceType))
            {
                parameterBuilder.Append(':').Append(expression.TargetResourceType);
            }

            // Add :iterate modifier if present
            if (expression.Iterate)
            {
                parameterBuilder.Append(":iterate");
            }

            var includeValue = parameterBuilder.ToString();
            if (!string.IsNullOrEmpty(includeValue))
            {
                AddParameterIfNotExists(includeParam, includeValue);
            }

            return base.VisitInclude(expression, context);
        }

        public override object VisitMissingSearchParameter(MissingSearchParameterExpression expression, object context)
        {
            // Handle :missing modifier
            var parameterName = expression.Parameter.Code + ":missing";
            var parameterValue = expression.IsMissing ? "true" : "false";

            AddParameterIfNotExists(parameterName, parameterValue);

            return base.VisitMissingSearchParameter(expression, context);
        }

        public override object VisitCompartment(CompartmentSearchExpression expression, object context)
        {
            // Handle reverse chained searches with _has syntax
            // Note: _has searches are complex and may require special handling
            // For now, reconstruct basic _has syntax
            if (!string.IsNullOrEmpty(expression.CompartmentType) && !string.IsNullOrEmpty(expression.CompartmentId))
            {
                // This is a simplified approach - complex _has queries may need more sophisticated handling
                var hasParameter = $"_has:{expression.CompartmentType}:patient:{expression.CompartmentId}";
                AddParameterIfNotExists("_has", hasParameter);
            }

            return base.VisitCompartment(expression, context);
        }

        public override object VisitUnion(UnionExpression expression, object context)
        {
            // Union expressions represent OR operations
            // Visit all sub-expressions and let them add their parameters
            foreach (var subExpression in expression.Expressions)
            {
                subExpression.AcceptVisitor(this, context);
            }

            return base.VisitUnion(expression, context);
        }

        public override object VisitMultiary(MultiaryExpression expression, object context)
        {
            // Handle AND/OR expressions at the visitor level
            if (expression.MultiaryOperation == MultiaryOperator.And)
            {
                // For AND operations, visit each sub-expression separately
                // This will create separate query parameters for each condition
                foreach (var subExpression in expression.Expressions)
                {
                    subExpression.AcceptVisitor(this, context);
                }
            }
            else if (expression.MultiaryOperation == MultiaryOperator.Or)
            {
                // For OR operations, we need to handle them as comma-separated values
                // This is more complex and should be handled at the SearchParameter level
                // For now, fall back to the default behavior
                return base.VisitMultiary(expression, context);
            }

            return base.VisitMultiary(expression, context);
        }

        /// <summary>
        /// Converts various expression types to their query parameter string representation.
        /// </summary>
        private static string ConvertExpressionToQueryValue(Expression expression)
        {
            return expression switch
            {
                BinaryExpression binary => ConvertBinaryExpression(binary),
                StringExpression str => ConvertStringExpression(str),
                MultiaryExpression multiary => ConvertMultiaryExpression(multiary),
                UnionExpression union => ConvertUnionExpression(union),
                NotExpression not => ConvertNotExpression(not),
                MissingFieldExpression missing => ConvertMissingFieldExpression(missing),
                InExpression<string> inExpr => string.Join(",", inExpr.Values),
                InExpression<int> inExpr => string.Join(",", inExpr.Values),
                InExpression<long> inExpr => string.Join(",", inExpr.Values),
                InExpression<decimal> inExpr => string.Join(",", inExpr.Values),
                InExpression<double> inExpr => string.Join(",", inExpr.Values),
                InExpression<float> inExpr => string.Join(",", inExpr.Values),
                SearchParameterExpression searchParam => ConvertSearchParameterExpression(searchParam),
                _ => ConvertGenericExpression(expression) // Enhanced fallback for reference expressions
            };
        }

        private static string ConvertSearchParameterExpression(SearchParameterExpression expression)
        {
            // For nested search parameter expressions, extract just the value
            return ConvertExpressionToQueryValue(expression.Expression);
        }

        private static string ConvertBinaryExpression(BinaryExpression expression)
        {
            var value = expression.Value?.ToString() ?? string.Empty;

            return expression.BinaryOperator switch
            {
                BinaryOperator.Equal => value,
                BinaryOperator.GreaterThan => "gt" + value,
                BinaryOperator.GreaterThanOrEqual => "ge" + value,
                BinaryOperator.LessThan => "lt" + value,
                BinaryOperator.LessThanOrEqual => "le" + value,
                BinaryOperator.NotEqual => "ne" + value,
                _ => value
            };
        }

        private static string ConvertStringExpression(StringExpression expression)
        {
            var value = expression.Value ?? string.Empty;

            return expression.StringOperator switch
            {
                StringOperator.StartsWith => value + "*",
                StringOperator.EndsWith => "*" + value,
                StringOperator.Contains => value, // For :contains modifier, use raw value
                StringOperator.Equals => value,
                StringOperator.NotStartsWith => "not:" + value + "*",
                StringOperator.NotEndsWith => "not:*" + value,
                StringOperator.NotContains => "not:" + value,
                _ => value
            };
        }

        private static string ConvertMultiaryExpression(MultiaryExpression expression)
        {
            // For OR expressions, join values with comma
            if (expression.MultiaryOperation == MultiaryOperator.Or)
            {
                return string.Join(",", expression.Expressions.Select(ConvertExpressionToQueryValue));
            }

            // For AND expressions, this should be handled at the visitor level
            // If we reach here, it means the visitor didn't handle it properly
            // Return the first expression as a fallback to avoid malformed queries
            return expression.Expressions.Count > 0
                ? ConvertExpressionToQueryValue(expression.Expressions[0])
                : string.Empty;
        }

        private static string ConvertNotExpression(NotExpression expression)
        {
            // For NotExpression, convert the inner expression
            // The :not modifier is handled at the parameter name level in VisitSearchParameter
            return ConvertExpressionToQueryValue(expression.Expression);
        }

        private static string ConvertMissingFieldExpression(MissingFieldExpression expression)
        {
            // FHIR missing field syntax uses :missing=true or :missing=false
            return "missing:true";
        }

        private static string ConvertUnionExpression(UnionExpression expression)
        {
            // Union expressions typically represent OR operations in FHIR
            // Join the sub-expressions with commas
            return string.Join(",", expression.Expressions.Select(ConvertExpressionToQueryValue));
        }

        /// <summary>
        /// Enhanced fallback method for converting expressions to query values, with special handling for reference expressions.
        /// </summary>
        private static string ConvertGenericExpression(Expression expression)
        {
            // Try to extract value using reflection for known expression types that contain Value properties
            var expressionType = expression.GetType();

            // Look for Value property (common in reference and other expressions)
            var valueProperty = expressionType.GetProperty("Value");
            if (valueProperty != null)
            {
                var value = valueProperty.GetValue(expression);
                if (value != null)
                {
                    // Handle reference values that might be Resource objects or strings
                    if (value is string stringValue)
                    {
                        return stringValue;
                    }
                    else if (value.GetType().Name.Contains("Resource", StringComparison.OrdinalIgnoreCase) || value.ToString().Contains('/', StringComparison.Ordinal))
                    {
                        // This looks like a reference - preserve the full value
                        return value.ToString();
                    }
                    else
                    {
                        return value.ToString();
                    }
                }
            }

            // Look for Values property (for collection-based expressions)
            var valuesProperty = expressionType.GetProperty("Values");
            if (valuesProperty != null && valuesProperty.PropertyType.IsGenericType)
            {
                var values = valuesProperty.GetValue(expression);
                if (values is System.Collections.IEnumerable enumerable)
                {
                    var stringValues = new List<string>();
                    foreach (var item in enumerable)
                    {
                        if (item != null)
                        {
                            stringValues.Add(item.ToString());
                        }
                    }
                    return string.Join(",", stringValues);
                }
            }

            // Check if this might be a reference expression by looking at the string representation
            var stringRepresentation = expression.ToString();

            // If the string contains reference patterns, preserve them
            if (stringRepresentation.Contains('/', StringComparison.Ordinal) &&
                (stringRepresentation.Contains("Patient/", StringComparison.Ordinal) ||
                 stringRepresentation.Contains("Organization/", StringComparison.Ordinal) ||
                 stringRepresentation.Contains("Practitioner/", StringComparison.Ordinal) ||
                 stringRepresentation.Contains("Device/", StringComparison.Ordinal) ||
                 stringRepresentation.Contains("Location/", StringComparison.Ordinal)))
            {
                return stringRepresentation;
            }

            // Default fallback
            return stringRepresentation;
        }

        /// <summary>
        /// Reconstructs a reference value from ReferenceResourceType and ReferenceResourceId components.
        /// </summary>
        private static string ReconstructReferenceValue(MultiaryExpression multiaryExpression)
        {
            string resourceType = null;
            string resourceId = null;

            foreach (var expr in multiaryExpression.Expressions)
            {
                if (expr is StringExpression stringExpr)
                {
                    // Use reflection to get the FieldName property
                    var fieldNameProperty = stringExpr.GetType().GetProperty("FieldName");
                    if (fieldNameProperty?.GetValue(stringExpr) is FieldName fieldName)
                    {
                        switch (fieldName)
                        {
                            case FieldName.ReferenceResourceType:
                                resourceType = stringExpr.Value;
                                break;
                            case FieldName.ReferenceResourceId:
                                resourceId = stringExpr.Value;
                                break;
                        }
                    }
                }
            }

            // If we found both components, reconstruct the full reference
            if (!string.IsNullOrEmpty(resourceType) && !string.IsNullOrEmpty(resourceId))
            {
                return $"{resourceType}/{resourceId}";
            }

            // If we couldn't reconstruct, fall back to the original conversion logic
            return ConvertMultiaryExpression(multiaryExpression);
        }

        /// <summary>
        /// Adds a parameter to the list if it doesn't already exist to prevent duplicates.
        /// </summary>
        private void AddParameterIfNotExists(string parameterName, string parameterValue)
        {
            var parameterKey = $"{parameterName}={parameterValue}";
            if (_addedParameters.Add(parameterKey))
            {
                _queryParameters.Add(Tuple.Create(parameterName, parameterValue));
            }
        }
    }
}