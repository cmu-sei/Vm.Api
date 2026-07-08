// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Player.Vm.Api.Infrastructure.OperationFilters
{
    /// <summary>
    /// Removes properties marked with <see cref="JsonIgnoreAttribute"/> from multipart/form-data
    /// request body schemas. Swashbuckle honors [JsonIgnore] for JSON bodies and query parameters
    /// (see <see cref="JsonIgnoreQueryOperationFilter"/>) but not for form-data bodies, so a route
    /// value bound onto the command (e.g. Id) would otherwise leak into the generated client as a
    /// duplicate form field.
    /// </summary>
    public class JsonIgnoreFormDataOperationFilter : IOperationFilter
    {
        private const string FormDataContentType = "multipart/form-data";

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.RequestBody?.Content == null ||
                !operation.RequestBody.Content.TryGetValue(FormDataContentType, out var mediaType) ||
                mediaType.Schema?.Properties == null)
            {
                return;
            }

            var ignoredFormParameters = context.ApiDescription.ParameterDescriptions
                .Where(d => d.Source.Id == "Form")
                .Where(d => ((DefaultModelMetadata)d.ModelMetadata)?.Attributes.PropertyAttributes
                    ?.Any(x => x is JsonIgnoreAttribute) ?? false)
                .Select(d => d.Name);

            foreach (var name in ignoredFormParameters)
            {
                mediaType.Schema.Properties.Remove(name);
                mediaType.Schema.Required?.Remove(name);
            }
        }
    }
}
