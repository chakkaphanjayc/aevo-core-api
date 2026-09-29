using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Runtime;

namespace Aevo.CoreApi.Data;

/// <summary>
/// Hub's catalog surface is a thin, tenant-safe boundary over the POS-owned
/// canonical catalog tables. Core is the only write path used by Hub and
/// records every mutation for audit and downstream application sync.
/// </summary>
public sealed partial class CoreDataStore
{
    private static readonly string[] HubCatalogChannels = ["POS", "QR", "KIOSK", "PICKUP", "STAFF", "API"];
    private static readonly JsonSerializerOptions HubCatalogJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<HubCatalogSnapshotRecord> ListHubCatalogAsync(
        Guid organizationId,
        Guid storeId,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "hub.catalog.list",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """
                    select
                      coalesce((
                        select jsonb_agg(jsonb_build_object(
                          'id', category.id,
                          'organizationId', category.organization_id,
                          'parentId', category.parent_id,
                          'code', category.code,
                          'name', category.name,
                          'slug', category.slug,
                          'sortOrder', category.sort_order,
                          'status', category.status
                        ) order by category.sort_order, category.name, category.id)
                        from public.categories category
                        where category.organization_id = @organization_id
                      ), '[]'::jsonb) as categories,
                      coalesce((
                        select jsonb_agg(jsonb_build_object(
                          'id', product.id,
                          'organizationId', product.organization_id,
                          'categoryId', product.category_id,
                          'sku', product.sku,
                          'name', product.name,
                          'description', product.description,
                          'basePriceMinor', product.base_price_minor,
                          'currency', product.currency,
                          'status', product.status,
                          'imageUrl', product.image_url,
                          'displayOrder', product.display_order,
                          'variants', coalesce((
                            select jsonb_agg(jsonb_build_object(
                              'id', variant.id,
                              'code', variant.code,
                              'name', variant.name,
                              'priceMinor', variant.price_minor,
                              'sortOrder', variant.sort_order,
                              'status', variant.status
                            ) order by variant.sort_order, variant.name, variant.id)
                            from public.product_variants variant
                            where variant.organization_id = product.organization_id
                              and variant.product_id = product.id
                          ), '[]'::jsonb),
                          'availability', coalesce((
                            select jsonb_agg(jsonb_build_object(
                              'channel', availability.channel,
                              'isAvailable', availability.is_available,
                              'soldOut', availability.sold_out,
                              'priceOverrideMinor', availability.price_override_minor
                            ) order by availability.channel)
                            from public.product_availability availability
                            where availability.organization_id = product.organization_id
                              and availability.store_id = @store_id
                              and availability.product_id = product.id
                          ), '[]'::jsonb),
                          'createdAt', product.created_at,
                          'updatedAt', product.updated_at
                        ) order by product.display_order, product.name, product.id)
                        from public.products product
                        where product.organization_id = @organization_id
                      ), '[]'::jsonb) as products,
                      coalesce((
                        select jsonb_agg(jsonb_build_object(
                          'id', menu.id,
                          'organizationId', menu.organization_id,
                          'storeId', menu.store_id,
                          'code', menu.code,
                          'name', menu.name,
                          'channel', menu.channel,
                          'status', menu.status,
                          'items', coalesce((
                            select jsonb_agg(jsonb_build_object(
                              'id', item.id,
                              'menuId', item.menu_id,
                              'productId', item.product_id,
                              'variantId', item.variant_id,
                              'priceOverrideMinor', item.price_override_minor,
                              'sortOrder', item.sort_order,
                              'isAvailable', item.is_available,
                              'soldOut', item.sold_out
                            ) order by item.sort_order, item.id)
                            from public.menu_items item
                            where item.organization_id = menu.organization_id
                              and item.menu_id = menu.id
                          ), '[]'::jsonb)
                        ) order by menu.name, menu.id)
                        from public.menus menu
                        where menu.organization_id = @organization_id
                          and menu.store_id = @store_id
                      ), '[]'::jsonb) as menus,
                      coalesce((
                        select jsonb_agg(jsonb_build_object(
                          'id', modifier_group.id,
                          'organizationId', modifier_group.organization_id,
                          'code', modifier_group.code,
                          'name', modifier_group.name,
                          'selectionType', modifier_group.selection_type,
                          'minSelections', modifier_group.min_selections,
                          'maxSelections', modifier_group.max_selections,
                          'required', modifier_group.required,
                          'modifiers', coalesce((
                            select jsonb_agg(jsonb_build_object(
                              'id', modifier.id,
                              'code', modifier.code,
                              'name', modifier.name,
                              'priceDeltaMinor', modifier.price_delta_minor,
                              'sortOrder', modifier.sort_order,
                              'status', modifier.status
                            ) order by modifier.sort_order, modifier.name, modifier.id)
                            from public.modifiers modifier
                            where modifier.organization_id = modifier_group.organization_id
                              and modifier.modifier_group_id = modifier_group.id
                          ), '[]'::jsonb),
                          'status', modifier_group.status
                        ) order by modifier_group.name, modifier_group.id)
                        from public.modifier_groups modifier_group
                        where modifier_group.organization_id = @organization_id
                      ), '[]'::jsonb) as modifier_groups,
                      coalesce((
                        select jsonb_agg(jsonb_build_object(
                          'productId', mapping.product_id,
                          'modifierGroupId', mapping.modifier_group_id,
                          'sortOrder', mapping.sort_order
                        ) order by mapping.product_id, mapping.sort_order, mapping.modifier_group_id)
                        from public.product_modifier_groups mapping
                        where mapping.organization_id = @organization_id
                      ), '[]'::jsonb) as product_modifier_groups
                    """,
                    connection);
                command.Parameters.AddWithValue("organization_id", organizationId);
                command.Parameters.AddWithValue("store_id", storeId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Catalog snapshot could not be loaded.");
                return new HubCatalogSnapshotRecord(
                    ReadJsonList<HubCategoryRecord>(reader.GetValue(0)),
                    ReadJsonList<HubProductRecord>(reader.GetValue(1)),
                    ReadJsonList<HubMenuRecord>(reader.GetValue(2)),
                    ReadJsonList<HubModifierGroupRecord>(reader.GetValue(3)),
                    ReadJsonList<HubProductModifierGroupRecord>(reader.GetValue(4)));
            });

    public async Task<HubProductRecord> CreateHubProductAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        JsonElement body,
        string requestId,
        CancellationToken cancellationToken)
    {
        var sku = RequiredJsonString(body, "sku", 64).Trim().ToUpperInvariant();
        var name = RequiredJsonString(body, "name", 160).Trim();
        var description = (JsonString(body, "description") ?? string.Empty).Trim();
        var basePriceMinor = JsonInt(body, "basePriceMinor") ?? 0;
        var currency = NormalizeCatalogCurrency(JsonString(body, "currency") ?? "THB");
        var categoryId = JsonGuid(body, "categoryId");
        var imageUrl = NormalizeCatalogImageUrl(JsonString(body, "imageUrl"));
        var displayOrder = JsonInt(body, "displayOrder") ?? 0;
        ValidateCatalogNumber(basePriceMinor, "basePriceMinor");
        ValidateCatalogNumber(displayOrder, "displayOrder");
        if (description.Length > 2000) throw new HubCatalogValidationException("PRODUCT_DESCRIPTION_TOO_LONG", "Product description must be 2,000 characters or fewer.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(connection, transaction, principal.UserId, principal.OrganizationId, storeId, "HUB", null, cancellationToken);
        try
        {
            await using var productCommand = new NpgsqlCommand(
                """
                insert into public.products
                  (organization_id, category_id, sku, name, description, base_price_minor, currency, status, image_url, display_order)
                values
                  (@organization_id, @category_id, @sku, @name, @description, @base_price_minor, @currency, 'ACTIVE', @image_url, @display_order)
                returning id
                """,
                connection,
                transaction);
            productCommand.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            AddNullableGuid(productCommand, "category_id", categoryId);
            productCommand.Parameters.AddWithValue("sku", sku);
            productCommand.Parameters.AddWithValue("name", name);
            productCommand.Parameters.AddWithValue("description", description);
            productCommand.Parameters.AddWithValue("base_price_minor", basePriceMinor);
            productCommand.Parameters.AddWithValue("currency", currency);
            AddNullableText(productCommand, "image_url", imageUrl);
            productCommand.Parameters.AddWithValue("display_order", displayOrder);
            var productId = (Guid?)await productCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new CoreDatabaseException("Product creation failed.");

            await using (var variantCommand = new NpgsqlCommand(
                "insert into public.product_variants (organization_id, product_id, code, name, price_minor, sort_order, status) values (@organization_id, @product_id, 'BASE', 'Standard', @price_minor, 0, 'ACTIVE')",
                connection,
                transaction))
            {
                variantCommand.Parameters.AddWithValue("organization_id", principal.OrganizationId);
                variantCommand.Parameters.AddWithValue("product_id", productId);
                variantCommand.Parameters.AddWithValue("price_minor", basePriceMinor);
                await variantCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var availabilityCommand = new NpgsqlCommand(
                "insert into public.product_availability (organization_id, store_id, product_id, channel, is_available, sold_out) select @organization_id, @store_id, @product_id, channel, true, false from unnest(@channels) as channel",
                connection,
                transaction))
            {
                availabilityCommand.Parameters.AddWithValue("organization_id", principal.OrganizationId);
                availabilityCommand.Parameters.AddWithValue("store_id", storeId);
                availabilityCommand.Parameters.AddWithValue("product_id", productId);
                availabilityCommand.Parameters.Add(new NpgsqlParameter("channels", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = HubCatalogChannels });
                await availabilityCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var product = await ReadHubProductAsync(connection, transaction, principal.OrganizationId, storeId, productId, cancellationToken)
                ?? throw new CoreDatabaseException("The product was created but could not be read back.");
            var after = JsonSerializer.SerializeToElement(product, HubCatalogJsonOptions);
            await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "HUB_PRODUCT_CREATED", "product", productId.ToString("D"), "Created product in the organization catalog.", null, after, requestId, cancellationToken);
            await InsertHubCatalogOutboxAsync(connection, transaction, "HUB_PRODUCT_CREATED", principal.OrganizationId, storeId, product, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return product;
        }
        catch (PostgresException error) when (error.SqlState is "23505" or "23503")
        {
            await transaction.RollbackAsync(cancellationToken);
            throw error.SqlState == "23505"
                ? new HubCatalogValidationException("PRODUCT_SKU_EXISTS", "A product with this SKU already exists in the organization.", 409)
                : new HubCatalogValidationException("PRODUCT_REFERENCE_INVALID", "The selected category or store reference is invalid.", 400);
        }
    }

    public async Task<HubProductRecord> UpdateHubProductAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        Guid productId,
        JsonElement body,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new HubCatalogValidationException("PRODUCT_BODY_REQUIRED", "Product changes are required.");
        var categorySet = HasJsonProperty(body, "categoryId");
        var nameSet = HasJsonProperty(body, "name");
        var descriptionSet = HasJsonProperty(body, "description");
        var priceSet = HasJsonProperty(body, "basePriceMinor");
        var currencySet = HasJsonProperty(body, "currency");
        var imageSet = HasJsonProperty(body, "imageUrl");
        var displayOrderSet = HasJsonProperty(body, "displayOrder");
        var statusSet = HasJsonProperty(body, "status");
        if (!categorySet && !nameSet && !descriptionSet && !priceSet && !currencySet && !imageSet && !displayOrderSet && !statusSet)
        {
            throw new HubCatalogValidationException("PRODUCT_CHANGES_REQUIRED", "Provide at least one product field to update.");
        }

        var name = nameSet ? RequiredJsonString(body, "name", 160).Trim() : null;
        var description = descriptionSet ? (JsonString(body, "description") ?? string.Empty).Trim() : null;
        var price = priceSet ? JsonInt(body, "basePriceMinor") : null;
        var currency = currencySet ? NormalizeCatalogCurrency(JsonString(body, "currency") ?? string.Empty) : null;
        var imageUrl = imageSet ? NormalizeCatalogImageUrl(JsonString(body, "imageUrl")) : null;
        var displayOrder = displayOrderSet ? JsonInt(body, "displayOrder") : null;
        var status = statusSet ? (JsonString(body, "status") ?? string.Empty).Trim().ToUpperInvariant() : null;
        if (description is not null && description.Length > 2000) throw new HubCatalogValidationException("PRODUCT_DESCRIPTION_TOO_LONG", "Product description must be 2,000 characters or fewer.");
        if (priceSet) ValidateCatalogNumber(price ?? -1, "basePriceMinor");
        if (displayOrderSet) ValidateCatalogNumber(displayOrder ?? -1, "displayOrder");
        if (status is not null && status is not ("ACTIVE" or "ARCHIVED")) throw new HubCatalogValidationException("PRODUCT_STATUS_INVALID", "Product status must be ACTIVE or ARCHIVED.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(connection, transaction, principal.UserId, principal.OrganizationId, storeId, "HUB", null, cancellationToken);
        try
        {
            var before = await ReadHubProductAsync(connection, transaction, principal.OrganizationId, storeId, productId, cancellationToken);
            if (before is null) throw new HubCatalogValidationException("PRODUCT_NOT_FOUND", "Product not found in the organization catalog.", 404);

            await using var command = new NpgsqlCommand(
                """
                update public.products
                set category_id = case when @category_set then @category_id else category_id end,
                    name = case when @name_set then @name else name end,
                    description = case when @description_set then @description else description end,
                    base_price_minor = case when @price_set then @base_price_minor else base_price_minor end,
                    currency = case when @currency_set then @currency else currency end,
                    image_url = case when @image_set then @image_url else image_url end,
                    display_order = case when @display_order_set then @display_order else display_order end,
                    status = case when @status_set then @status else status end,
                    updated_at = timezone('utc', now())
                where organization_id = @organization_id and id = @product_id
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            command.Parameters.AddWithValue("product_id", productId);
            command.Parameters.AddWithValue("category_set", categorySet);
            AddNullableGuid(command, "category_id", categorySet ? JsonGuid(body, "categoryId") : null);
            command.Parameters.AddWithValue("name_set", nameSet);
            AddNullableText(command, "name", name);
            command.Parameters.AddWithValue("description_set", descriptionSet);
            AddNullableText(command, "description", description);
            command.Parameters.AddWithValue("price_set", priceSet);
            AddNullableInt(command, "base_price_minor", price);
            command.Parameters.AddWithValue("currency_set", currencySet);
            AddNullableText(command, "currency", currency);
            command.Parameters.AddWithValue("image_set", imageSet);
            AddNullableText(command, "image_url", imageUrl);
            command.Parameters.AddWithValue("display_order_set", displayOrderSet);
            AddNullableInt(command, "display_order", displayOrder);
            command.Parameters.AddWithValue("status_set", statusSet);
            AddNullableText(command, "status", status);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new HubCatalogValidationException("PRODUCT_NOT_FOUND", "Product not found in the organization catalog.", 404);

            var afterProduct = await ReadHubProductAsync(connection, transaction, principal.OrganizationId, storeId, productId, cancellationToken)
                ?? throw new CoreDatabaseException("The product update could not be read back.");
            var beforeJson = JsonSerializer.SerializeToElement(before, HubCatalogJsonOptions);
            var afterJson = JsonSerializer.SerializeToElement(afterProduct, HubCatalogJsonOptions);
            await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "HUB_PRODUCT_UPDATED", "product", productId.ToString("D"), "Updated product details from Hub.", beforeJson, afterJson, requestId, cancellationToken);
            await InsertHubCatalogOutboxAsync(connection, transaction, "HUB_PRODUCT_UPDATED", principal.OrganizationId, storeId, afterProduct, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return afterProduct;
        }
        catch (PostgresException error) when (error.SqlState is "23503" or "23522")
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new HubCatalogValidationException("PRODUCT_REFERENCE_INVALID", "The selected category or product value is invalid.");
        }
    }

    public async Task<HubAvailabilityRecord> PatchHubAvailabilityAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        Guid productId,
        JsonElement body,
        string requestId,
        CancellationToken cancellationToken)
    {
        var channel = (JsonString(body, "channel") ?? string.Empty).Trim().ToUpperInvariant();
        if (!HubCatalogChannels.Contains(channel, StringComparer.Ordinal)) throw new HubCatalogValidationException("CATALOG_CHANNEL_INVALID", "Choose a supported catalog channel.");
        var isAvailable = JsonBool(body, "isAvailable");
        var soldOut = JsonBool(body, "soldOut");
        var priceSet = HasJsonProperty(body, "priceOverrideMinor");
        var price = JsonNullableInt(body, "priceOverrideMinor");
        if (priceSet && price is < 0) throw new HubCatalogValidationException("PRICE_OVERRIDE_INVALID", "Price override must be zero or greater.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(connection, transaction, principal.UserId, principal.OrganizationId, storeId, "HUB", null, cancellationToken);
        try
        {
            var product = await ReadHubProductAsync(connection, transaction, principal.OrganizationId, storeId, productId, cancellationToken);
            if (product is null) throw new HubCatalogValidationException("PRODUCT_NOT_FOUND", "Product not found in the organization catalog.", 404);
            var before = await ReadJsonAsync(
                connection,
                transaction,
                "select jsonb_build_object('channel', channel, 'isAvailable', is_available, 'soldOut', sold_out, 'priceOverrideMinor', price_override_minor) from public.product_availability where organization_id=@organization_id and store_id=@store_id and product_id=@product_id and channel=@channel",
                cancellationToken,
                ("organization_id", principal.OrganizationId),
                ("store_id", storeId),
                ("product_id", productId),
                ("channel", channel));

            await using var command = new NpgsqlCommand(
                """
                insert into public.product_availability
                  (organization_id, store_id, product_id, channel, is_available, sold_out, price_override_minor)
                values
                  (@organization_id, @store_id, @product_id, @channel, coalesce(@is_available, true), coalesce(@sold_out, false), @price_override)
                on conflict (organization_id, store_id, product_id, channel) do update set
                  is_available = coalesce(@is_available, product_availability.is_available),
                  sold_out = coalesce(@sold_out, product_availability.sold_out),
                  price_override_minor = case when @price_set then @price_override else product_availability.price_override_minor end,
                  updated_at = timezone('utc', now())
                returning channel, is_available, sold_out, price_override_minor
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            command.Parameters.AddWithValue("store_id", storeId);
            command.Parameters.AddWithValue("product_id", productId);
            command.Parameters.AddWithValue("channel", channel);
            AddNullableBool(command, "is_available", isAvailable);
            AddNullableBool(command, "sold_out", soldOut);
            AddNullableInt(command, "price_override", price);
            command.Parameters.AddWithValue("price_set", priceSet);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Availability update failed.");
            var availability = new HubAvailabilityRecord(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), NullableInt32(reader, 3));
            await reader.CloseAsync();

            var after = JsonSerializer.SerializeToElement(availability, HubCatalogJsonOptions);
            await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "HUB_PRODUCT_AVAILABILITY_UPDATED", "product_availability", $"{storeId:D}:{productId:D}:{channel}", "Updated store catalog availability.", before, after, requestId, cancellationToken);
            await InsertHubCatalogOutboxAsync(connection, transaction, "HUB_PRODUCT_AVAILABILITY_UPDATED", principal.OrganizationId, storeId, product with { Availability = [availability] }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return availability;
        }
        catch (PostgresException error) when (error.SqlState == "23503")
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new HubCatalogValidationException("PRODUCT_REFERENCE_INVALID", "The selected product or store reference is invalid.");
        }
    }

    private static async Task<HubProductRecord?> ReadHubProductAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid storeId,
        Guid productId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select jsonb_build_object(
              'id', product.id,
              'organizationId', product.organization_id,
              'categoryId', product.category_id,
              'sku', product.sku,
              'name', product.name,
              'description', product.description,
              'basePriceMinor', product.base_price_minor,
              'currency', product.currency,
              'status', product.status,
              'imageUrl', product.image_url,
              'displayOrder', product.display_order,
              'variants', coalesce((
                select jsonb_agg(jsonb_build_object(
                  'id', variant.id,
                  'code', variant.code,
                  'name', variant.name,
                  'priceMinor', variant.price_minor,
                  'sortOrder', variant.sort_order,
                  'status', variant.status
                ) order by variant.sort_order, variant.name, variant.id)
                from public.product_variants variant
                where variant.organization_id = product.organization_id and variant.product_id = product.id
              ), '[]'::jsonb),
              'availability', coalesce((
                select jsonb_agg(jsonb_build_object(
                  'channel', availability.channel,
                  'isAvailable', availability.is_available,
                  'soldOut', availability.sold_out,
                  'priceOverrideMinor', availability.price_override_minor
                ) order by availability.channel)
                from public.product_availability availability
                where availability.organization_id = product.organization_id
                  and availability.store_id = @store_id
                  and availability.product_id = product.id
              ), '[]'::jsonb),
              'createdAt', product.created_at,
              'updatedAt', product.updated_at
            )
            from public.products product
            where product.organization_id = @organization_id and product.id = @product_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("product_id", productId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : ReadJsonValue<HubProductRecord>(value);
    }

    private static async Task InsertHubCatalogOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        Guid organizationId,
        Guid storeId,
        HubProductRecord product,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            organizationId,
            storeId,
            productId = product.Id,
            sku = product.Sku,
            status = product.Status,
            updatedAt = product.UpdatedAt
        }, HubCatalogJsonOptions);
        await using var command = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values (@event_type, 'product', @aggregate_id, @payload)",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("aggregate_id", $"{organizationId:D}:{storeId:D}:{product.Id:D}");
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static List<T> ReadJsonList<T>(object value)
    {
        var result = JsonSerializer.Deserialize<List<T>>(JsonText(value, "[]"), HubCatalogJsonOptions);
        return result ?? [];
    }

    private static T ReadJsonValue<T>(object value)
        => JsonSerializer.Deserialize<T>(JsonText(value, "{}"), HubCatalogJsonOptions)
            ?? throw new CoreDatabaseException("Core returned an invalid catalog payload.");

    private static string JsonText(object? value, string fallback)
    {
        if (value is null or DBNull) return fallback;
        if (value is JsonDocument document) return document.RootElement.GetRawText();
        if (value is JsonElement element) return element.GetRawText();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
    }

    private static bool HasJsonProperty(JsonElement body, string name)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out _);

    private static string NormalizeCatalogCurrency(string value)
    {
        var currency = value.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new HubCatalogValidationException("PRODUCT_CURRENCY_INVALID", "Currency must be a three-letter ISO code.");
        }
        return currency;
    }

    private static string? NormalizeCatalogImageUrl(string? value)
    {
        var imageUrl = value?.Trim();
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;
        if (imageUrl.Length > 2000 || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new HubCatalogValidationException("PRODUCT_IMAGE_URL_INVALID", "Image URL must be an HTTP or HTTPS URL up to 2,000 characters.");
        }
        return imageUrl;
    }

    private static void ValidateCatalogNumber(int value, string field)
    {
        if (value < 0) throw new HubCatalogValidationException("PRODUCT_VALUE_INVALID", $"{field} must be zero or greater.");
    }
}
