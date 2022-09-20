// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class StringExtensions
    {
        /// <summary>
        /// Converts a string to CamelCase, assuming it is already in TitleCase
        /// </summary>
        public static string TitleCaseToCamelCase(this string str)
        {
            var camelCaseStr = str;

            if (!string.IsNullOrEmpty(str) && str.Length > 1)
            {
                camelCaseStr = Char.ToLowerInvariant(camelCaseStr[0]) + camelCaseStr.Substring(1);
            }

            return camelCaseStr;
        }

        /// <summary>
        /// Naive pluralizer that adds an s to the end of a string if count is not 1
        /// </summary>
        public static string Pluralize(this string str, int count)
        {
            if (count == 1)
            {
                return str;
            }

            return str + 's';
        }
    }
}
