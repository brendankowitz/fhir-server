// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using SearchPredicate = System.Func<System.Collections.Generic.IEnumerable<(SamplesFileStorageProvider.ResourceLocation Location, System.Collections.Generic.IReadOnlyCollection<Microsoft.Health.Fhir.Core.Features.Search.SearchIndexEntry> Index)>, System.Collections.Generic.IEnumerable<(SamplesFileStorageProvider.ResourceLocation, System.Collections.Generic.IReadOnlyCollection<Microsoft.Health.Fhir.Core.Features.Search.SearchIndexEntry>)>>;

namespace SamplesFileStorageProvider
{
    internal class SearchQueryInterpreter : IExpressionVisitorWithInitialContext<SearchQueryInterpreter.Context, SearchPredicate>
    {
        Context IExpressionVisitorWithInitialContext<Context, SearchPredicate>.InitialContext => default;

        public SearchPredicate VisitSearchParameter(SearchParameterExpression expression, Context context)
        {
            return AppendSubquery(expression.Parameter.Name, expression.Expression, context);
        }

        public SearchPredicate VisitBinary(BinaryExpression expression, Context context)
        {
            SearchPredicate filter = input =>
            {
                return input.Where(x => x.Index.Any(y => y.SearchParameter.Name == context.ParameterName &&
                                                         GetMappedValue(expression.BinaryOperator, y.Value, (IComparable)expression.Value)));
            };

            return filter;
        }

        private static bool GetMappedValue(BinaryOperator expressionBinaryOperator, ISearchValue first, IComparable second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            var comparisonVisitor = new ComparisonValueVisitor(expressionBinaryOperator, second);

            first.AcceptVisitor(comparisonVisitor);

            return comparisonVisitor.Compare();
        }

        public SearchPredicate VisitChained(ChainedExpression expression, Context context)
        {
            throw new System.NotImplementedException();
        }

        public SearchPredicate VisitMissingField(MissingFieldExpression expression, Context context)
        {
            throw new System.NotImplementedException();
        }

        public SearchPredicate VisitMissingSearchParameter(MissingSearchParameterExpression expression, Context context)
        {
            throw new System.NotImplementedException();
        }

        public SearchPredicate VisitMultiary(MultiaryExpression expression, Context context)
        {
            SearchPredicate filter = input =>
            {
                var results = expression.Expressions.Select(x => x.AcceptVisitor(this, context))
                    .Aggregate((x, y) =>
                    {
                        switch (expression.MultiaryOperation)
                        {
                            case MultiaryOperator.And:
                                return p => x(p).Intersect(y(p));
                            case MultiaryOperator.Or:
                                return p => x(p).Union(y(p));
                            default:
                                throw new NotImplementedException();
                        }
                    });

                return results(input);
            };

            return filter;
        }

        public SearchPredicate VisitString(StringExpression expression, Context context)
        {
            StringComparison comparison = expression.IgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            SearchPredicate filter;

            if (context.ParameterName == "_type")
            {
                filter = input => input.Where(x => x.Location.ResourceType.Equals(expression.Value, comparison));
            }
            else
            {
                switch (expression.StringOperator)
                {
                    case StringOperator.StartsWith:
                        filter = input => input.Where(x => x.Index.Any(y => y.SearchParameter.Name == context.ParameterName &&
                                                                            CompareStringParameter(y, (a, b, c) => a.StartsWith(b, c))));
                        break;
                    case StringOperator.Equals:
                        filter = input => input.Where(x => x.Index.Any(y => y.SearchParameter.Name == context.ParameterName &&
                                                                            CompareStringParameter(y, string.Equals)));
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }

            bool CompareStringParameter(SearchIndexEntry entry, Func<string, string, StringComparison, bool> compareFunc)
            {
                switch (entry.SearchParameter.Type)
                {
                    case Microsoft.Health.Fhir.ValueSets.SearchParamType.String:
                        return compareFunc(((StringSearchValue)entry.Value).String, expression.Value, comparison);

                    case Microsoft.Health.Fhir.ValueSets.SearchParamType.Token:
                        return compareFunc(((TokenSearchValue)entry.Value).Code, expression.Value, comparison) ||
                               compareFunc(((TokenSearchValue)entry.Value).System, expression.Value, comparison);
                    default:
                        throw new NotImplementedException();
                }
            }

            return filter;
        }

        public SearchPredicate VisitCompartment(CompartmentSearchExpression expression, Context context)
        {
            throw new System.NotImplementedException();
        }

        public SearchPredicate VisitInclude(IncludeExpression expression, Context context)
        {
            throw new System.NotImplementedException();
        }

        private SearchPredicate AppendSubquery(string parameterName, Expression expression, Context context, bool negate = false)
        {
            EnsureArg.IsNotNull(parameterName, nameof(parameterName));

            var newContext = context.WithParameterName(parameterName);

            SearchPredicate filter = input =>
            {
                if (expression != null)
                {
                    return expression.AcceptVisitor(this, newContext)(input);
                }
                else
                {
                    throw new NotSupportedException();
                }
            };

            if (negate)
            {
                SearchPredicate inner = filter;
                filter = input => input.Except(inner(input));
            }

            return filter;
        }

        /// <summary>
        /// Context that is passed through the visit.
        /// </summary>
        internal struct Context
        {
            public string ParameterName { get; set; }

            public Context WithParameterName(string paramName)
            {
                return new Context
                {
                    ParameterName = paramName,
                };
            }
        }
    }
}
