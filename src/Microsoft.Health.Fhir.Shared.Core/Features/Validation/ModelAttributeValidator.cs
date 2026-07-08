// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using Hl7.Fhir.Validation;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Validation
{
    public class ModelAttributeValidator : IModelAttributeValidator
    {
        private static readonly Regex DateTimeLiteralErrorRegex = new Regex(
            @"'(?<literal>[^']+)' is not a correct literal for a dateTime",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex DateTimeOffsetWithoutTimeRegex = new Regex(
            @"^\d{4}(-\d{2}(-\d{2})?)?(Z|[+-]\d{2}:\d{2})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public bool TryValidate(ResourceElement value, ICollection<ValidationResult> validationResults = null, bool recurse = false)
        {
            var resource = value.ToPoco();
            var isValid = DotNetAttributeValidation.TryValidate(resource, validationResults, recurse);
            if (recurse || !isValid)
            {
                return isValid;
            }

            var recursiveValidationResults = new List<ValidationResult>();
            DotNetAttributeValidation.TryValidate(resource, recursiveValidationResults, recurse: true);

            var primitiveFormatResults = recursiveValidationResults
                .Where(IsDateTimeOffsetWithoutTimeError)
                .Select(x => new ValidationResult(
                    $"Invalid primitive format: {x.ErrorMessage}",
                    x.MemberNames))
                .ToList();

            if (primitiveFormatResults.Count == 0)
            {
                return true;
            }

            if (validationResults != null)
            {
                foreach (var result in primitiveFormatResults)
                {
                    validationResults.Add(result);
                }
            }

            return false;
        }

        private static bool IsDateTimeOffsetWithoutTimeError(ValidationResult result)
        {
            if (result?.ErrorMessage == null)
            {
                return false;
            }

            var match = DateTimeLiteralErrorRegex.Match(result.ErrorMessage);
            return match.Success && DateTimeOffsetWithoutTimeRegex.IsMatch(match.Groups["literal"].Value);
        }
    }
}
