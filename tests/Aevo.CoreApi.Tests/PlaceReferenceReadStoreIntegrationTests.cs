using Aevo.CoreApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceReferenceReadStoreIntegrationTests
{
    [Fact]
    public async Task LoadsExplicitMappingAndRedirectChainForTheSharedResolver()
    {
        if (!DatabaseConfigured() || !IntegrationEnabled()) return;

        await using var connection = await OpenConnectionAsync();
        var sourcePlaceId = Guid.NewGuid();
        var targetPlaceId = Guid.NewGuid();
        var marker = $"place-reference-{Guid.NewGuid():N}";
        var externalId = $"store-{marker}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_DATABASE_URL"] = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")
            })
            .Build();
        await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);

        try
        {
            await ExecuteAsync(
                connection,
                "insert into aevo_place_registry (place_id, slug, name, status, source_revision) values (@place_id, @slug, @name, 'visible', @source_revision)",
                ("place_id", sourcePlaceId),
                ("slug", $"{marker}-source"),
                ("name", "Reference source fixture"),
                ("source_revision", marker));
            await ExecuteAsync(
                connection,
                "insert into aevo_place_registry (place_id, slug, name, status, source_revision) values (@place_id, @slug, @name, 'visible', @source_revision)",
                ("place_id", targetPlaceId),
                ("slug", $"{marker}-target"),
                ("name", "Reference target fixture"),
                ("source_revision", marker));
            await ExecuteAsync(
                connection,
                "insert into aevo_place_legacy_mappings (namespace, external_id, source_version, place_id, match_status, match_reason) values ('aevo.store', @external_id, 'unversioned', @place_id, 'linked', 'test mapping')",
                ("external_id", externalId),
                ("place_id", sourcePlaceId));
            await ExecuteAsync(
                connection,
                "insert into aevo_place_redirects (source_place_id, target_place_id, status, reason) values (@source_place_id, @target_place_id, 'active', 'merged')",
                ("source_place_id", sourcePlaceId),
                ("target_place_id", targetPlaceId));

            var resolutions = await database.ResolvePlaceReferencesAsync(
                new[]
                {
                    new PlaceReference(PlaceReferenceSurface.Feed, "aevo.store", externalId),
                    new PlaceReference(PlaceReferenceSurface.Url, PlaceReferenceResolver.CanonicalNamespace, sourcePlaceId.ToString("D"))
                },
                CancellationToken.None);

            Assert.Equal(2, resolutions.Count);
            Assert.All(resolutions, resolution =>
            {
                Assert.Equal(PlaceReferenceResolutionStatus.Redirected, resolution.Status);
                Assert.Equal(sourcePlaceId, resolution.RequestedPlaceId);
                Assert.Equal(targetPlaceId, resolution.ResolvedPlaceId);
                Assert.Equal(1, resolution.RedirectHops);
                Assert.Equal("merged", resolution.RedirectReason);
            });
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "delete from aevo_place_legacy_mappings where external_id = @external_id",
                ("external_id", externalId));
            await ExecuteAsync(
                connection,
                "delete from aevo_place_redirects where source_place_id = @source_place_id",
                ("source_place_id", sourcePlaceId));
            await ExecuteAsync(
                connection,
                "delete from aevo_place_registry where place_id = any(@place_ids)",
                ("place_ids", new[] { sourcePlaceId, targetPlaceId }));
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var raw = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim()
            ?? throw new InvalidOperationException("AEVO_DATABASE_URL is not configured.");
        var connection = new NpgsqlConnection(NormalizeDatabaseConnectionString(raw));
        await connection.OpenAsync();
        return connection;
    }

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "postgres" and not "postgresql"))
        {
            throw new InvalidOperationException("The database integration URL is not PostgreSQL.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ? "postgres" : uri.AbsolutePath.Trim('/')
        };
        var userInfo = uri.UserInfo;
        if (!string.IsNullOrWhiteSpace(userInfo))
        {
            var separator = userInfo.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(separator >= 0 ? userInfo[..separator] : userInfo);
            if (separator >= 0) builder.Password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
        }
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2) continue;
                var key = Uri.UnescapeDataString(parts[0]);
                var queryValue = Uri.UnescapeDataString(parts[1]);
                if (string.Equals(key, "sslmode", StringComparison.OrdinalIgnoreCase)) builder.SslMode = ParseSslMode(queryValue);
            }
        }
        return builder.ConnectionString;
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => SslMode.Prefer
    };

    private static bool DatabaseConfigured() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVO_DATABASE_URL"));

    private static bool IntegrationEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("AEVO_PLACE_REFERENCE_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}
