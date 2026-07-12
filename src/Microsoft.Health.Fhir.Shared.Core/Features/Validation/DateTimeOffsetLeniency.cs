// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Microsoft.Health.Fhir.Core.Features.Validation
{
    /// <summary>
    /// Detects a single, narrow dateTime leniency: the FHIR spec requires a timezone offset whenever a
    /// time-of-day component is present, but legacy/pre-existing FHIR data commonly omits it. The server
    /// tolerates exactly this one shape (date + time-of-day, offset missing, e.g. "1980-05-11T16:32:15")
    /// for backward compatibility; every other malformed dateTime literal is still rejected.
    /// </summary>
    /// <remarks>
    /// <see cref="ModelAttributeValidator"/> already grants this leniency for non-Ignixa resources, as a
    /// side effect of a narrower regex (<c>DateTimeOffsetWithoutTimeRegex</c>) that identifies the
    /// *opposite* shape - offset present, time missing (e.g. "2021-10-13+02:00") - as the one recursive
    /// literal-format failure that still counts as an error; anything else recursive validation turns up,
    /// including the missing-offset-with-time-present case, is silently tolerated by omission. That
    /// mechanism is specific to Firely's message format and existing control flow, so it is not reused
    /// directly here. This type exists so <see cref="IgnixaResourceValidator"/> can apply the same
    /// functional carve-out against Ignixa's own (differently worded) validation issues without
    /// duplicating the "what does a tolerable dateTime literal look like" pattern-matching logic inline.
    /// </remarks>
    internal static class DateTimeOffsetLeniency
    {
        private static readonly Regex MissingOffsetWithTimeRegex = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns true when <paramref name="literal"/> is exactly the tolerated shape: a date and
        /// time-of-day with the mandatory-per-spec timezone offset missing.
        /// </summary>
        internal static bool IsMissingOffsetWithTimePresent(string literal)
        {
            return !string.IsNullOrEmpty(literal) && MissingOffsetWithTimeRegex.IsMatch(literal);
        }
    }
}
