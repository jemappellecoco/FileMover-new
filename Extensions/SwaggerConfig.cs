// Extensions/SwaggerConfig.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using System.Reflection;

namespace FileMoverWeb.Extensions
{
    public static class SwaggerConfig
    {
        public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
        {
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "FileMover API",
                    Version = "v1",
                    Description = "多檔搬運任務（依 DestId 分組進度）",
                    Contact = new OpenApiContact
                    {
                        Name = "Stonebooks Studio",
                        Email = "support@stonebooks.tw"
                    }
                });

                // XML 註解（有檔才載）
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                    c.IncludeXmlComments(xmlPath);

                // ❗先不要 ExampleFilters，先讓 swagger 活著
                // c.ExampleFilters();
            });

            // ❗先不要掃 Example assembly，先讓 swagger 活著
            // services.AddSwaggerExamplesFromAssemblyOf<MoveController>();

            return services;
        }

        public static IApplicationBuilder UseSwaggerDocumentation(this IApplicationBuilder app)
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "FileMover v1");
                c.RoutePrefix = "swagger";
                c.DocumentTitle = "FileMover API Docs";
                c.DisplayRequestDuration();
                c.EnableFilter();
            });
            return app;
        }
    }
}
