using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddModelMetadataAnnotationService(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelMetadataAnnotationService, ModelMetadataAnnotationService>();
        return services;
    }
}
