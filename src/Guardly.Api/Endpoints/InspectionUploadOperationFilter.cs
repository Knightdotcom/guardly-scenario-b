using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Guardly.Api.Endpoints;

/// <summary>
/// Beskriver formuläret för POST /inspections i Swagger.
///
/// Endpointen läser formuläret själv (för att kunna ge begripliga felmeddelanden),
/// så Swashbuckle kan inte räkna ut fälten. Utan det här filtret visar Swagger UI
/// bara en namnlös fil, och uppladdningen nekas med "siteId saknas".
/// </summary>
public class InspectionUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(description.RelativePath?.TrimEnd('/'), "inspections", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Required = new HashSet<string> { "image", "siteId" },
                        Properties = new Dictionary<string, OpenApiSchema>
                        {
                            ["image"] = new()
                            {
                                Type = "string",
                                Format = "binary",
                                Description = "Bilden som ska analyseras (.jpg, .jpeg, .png, .bmp, .webp)"
                            },
                            ["siteId"] = new()
                            {
                                Type = "string",
                                Description = "Arbetsplatsen bilden hör till, t.ex. kvarteret-vallgatan"
                            },
                            ["zone"] = new()
                            {
                                Type = "string",
                                Description = "Valfri zonbeteckning, t.ex. Plan 3, östra gaveln"
                            },
                            ["uploadedBy"] = new()
                            {
                                Type = "string",
                                Description = "Valfritt: vem som laddade upp bilden"
                            }
                        }
                    },
                    Encoding =
                    {
                        ["image"] = new OpenApiEncoding { ContentType = "image/*" }
                    }
                }
            }
        };
    }
}
