// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Extensions
{
    public static class StringExtensions
    {
        public static bool Contains(this string str, string val, StringComparison comparison)
        {
            return str.IndexOf(val, comparison) > -1;
        }
    }
}
