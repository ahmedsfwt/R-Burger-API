using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RBurger.Application.Common.Interfaces;
using RBurger.Infrastructure.Authentication;
using RBurger.Infrastructure.Caching;
using RBurger.Infrastructure.Payments;
using RBurger.Infrastructure.Persistence;
using RBurger.Infrastructure.Persistence.Repositories;
using RBurger.Infrastructure.Realtime;
using RBurger.Infrastructure.Storage;

namespace RBurger.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.Configure<JwtSettings>(options =>
        {
            var section = configuration.GetSection(JwtSettings.SectionName);
            options.Issuer = section["Issuer"] ?? string.Empty;
            options.Audience = section["Audience"] ?? string.Empty;
            options.Key = section["Key"] ?? string.Empty;
        });

        // Day 13 addition. TTL not documented in v1.2 (only the access token's
        // ExpiresInSeconds=3600 is) - see RefreshTokenSettings' XML comment. Approved default:
        // 30 days, overridable via the "RefreshToken:ExpiresInDays" configuration key.
        services.Configure<RefreshTokenSettings>(options =>
        {
            var section = configuration.GetSection(RefreshTokenSettings.SectionName);
            var configuredDays = section["ExpiresInDays"];
            if (int.TryParse(configuredDays, out var days) && days > 0)
            {
                options.ExpiresInDays = days;
            }
        });

        // Day 15 addition (Backend Parity Spec §12).
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();

        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IPasswordHasher, PasswordHasherService>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IDriverRepository, DriverRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IBranchRepository, BranchRepository>();
        services.AddScoped<IMenuItemRepository, MenuItemRepository>();
        services.AddScoped<IReviewRepository, ReviewRepository>();

        // Day 15 addition (§7.3 public endpoints).
        services.AddScoped<IBuilderOptionGroupRepository, BuilderOptionGroupRepository>();

        // Day 10 additions (§7.6 Admin surface).
        services.AddScoped<IAdminRepository, AdminRepository>();
        services.AddScoped<IMenuCategoryRepository, MenuCategoryRepository>();

        // Day 13 additions (approved schema change - refresh-token persistence and
        // database-backed idempotency, resolving the Day 3/Day 12 documentation blockers).
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRecordRepository>();

        // Scaffold-only per the approved Blocking Issue #2 decision - always throws
        // StorageNotConfiguredException until a real AWSSDK.S3-backed implementation replaces
        // it (no AWS bucket/region/credentials/CDN domain are specified in Documentation
        // v1.2). See NotConfiguredMenuItemImageStorage's XML comment.
        services.Configure<S3Settings>(configuration.GetSection(S3Settings.SectionName));

        services.AddSingleton<IAmazonS3>(_ =>
        {
            var region = configuration["AWS:Region"];
            return new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        });

        services.AddScoped<IMenuItemImageStorage, S3MenuItemImageStorage>();

        // Day 12 addition (Blocking Issue #3, same scaffold-only approach as the image storage
        // registration above - approved). No Paymob/Fawry credentials are specified in
        // Documentation v1.2, so this always throws PaymentProviderNotConfiguredException.
        services.Configure<PaymobSettings>(options =>
        {
            var section = configuration.GetSection(PaymobSettings.SectionName);
            options.BaseUrl = section["BaseUrl"] ?? options.BaseUrl;
            options.SecretKey = section["SecretKey"] ?? string.Empty;
            options.HmacSecret = section["HmacSecret"] ?? string.Empty;
            options.NotificationUrl = section["NotificationUrl"] ?? string.Empty;
            if (int.TryParse(section["IntegrationId"], out var integrationId))
            {
                options.IntegrationId = integrationId;
            }
        });
        services.AddHttpClient<IPaymentProvider, PaymobPaymentProvider>();

        // Day 7 addition (§8): real-time order-status broadcasting.
        services.AddSignalR();
        services.AddScoped<IOrderRealtimeNotifier, SignalROrderRealtimeNotifier>();

        return services;
    }
}