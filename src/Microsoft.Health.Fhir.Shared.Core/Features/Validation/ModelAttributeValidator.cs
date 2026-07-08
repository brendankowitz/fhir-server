// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Hl7.Fhir.Validation;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Validation
{
    public class ModelAttributeValidator : IModelAttributeValidator
    {
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
                .Where(x => x.ErrorMessage?.Contains("correct literal", System.StringComparison.OrdinalIgnoreCase) == true)
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
    }
}
