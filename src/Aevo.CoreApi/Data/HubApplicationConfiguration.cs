using System.Text.Json;

namespace Aevo.CoreApi.Data;

internal sealed record HubApplicationConfigDefinition(
    string ApplicationCode,
    string SchemaRef,
    string SchemaVersion,
    string Label,
    string Description,
    IReadOnlyList<HubApplicationConfigFieldDefinition> Fields);

internal sealed record HubApplicationConfigFieldDefinition(
    string Key,
    string Label,
    string Description,
    string Type,
    bool Required,
    object DefaultValue,
    int? Min = null,
    int? Max = null,
    int? MaxLength = null,
    IReadOnlyList<HubApplicationConfigOptionRecord>? Options = null);

internal static class HubApplicationConfigurationCatalog
{
    private static readonly IReadOnlyList<HubApplicationConfigDefinition> Definitions =
    [
        new(
            "PLAY",
            "booking.v1",
            "1",
            "Booking operations",
            "Store-level booking defaults consumed by Aevo Play.",
            [
                Field("bookingEnabled", "Bookings enabled", "Allow customers to create bookings for this store.", "boolean", false, true),
                Field("slotDurationMinutes", "Slot duration", "Default length for a bookable slot.", "select", false, "60", options: Options(("30", "30 minutes"), ("60", "60 minutes"), ("90", "90 minutes"), ("120", "120 minutes"))),
                Field("advanceBookingDays", "Advance booking window", "How far ahead customers may book.", "integer", false, 30, min: 1, max: 365)
            ]),
        new(
            "POS",
            "pos.catalog.v1",
            "1",
            "Catalog behavior",
            "Store-level catalog presentation defaults consumed by Aevo POS or Kiosk.",
            [
                Field("catalogChannel", "Default catalog channel", "The default channel used when a catalog surface opens.", "select", false, "POS", options: Options(("POS", "POS"), ("QR", "QR ordering"), ("PICKUP", "Pickup"))),
                Field("allowPriceOverride", "Allow price overrides", "Permit an authorized operator to override a catalog price at checkout.", "boolean", false, false),
                Field("outOfStockMode", "Out-of-stock display", "Choose whether unavailable products are hidden or shown as sold out.", "select", false, "HIDE", options: Options(("HIDE", "Hide unavailable items"), ("SHOW_SOLD_OUT", "Show as sold out")))
            ]),
        new(
            "KIOSK",
            "pos.catalog.v1",
            "1",
            "Kiosk catalog behavior",
            "Store-level catalog presentation defaults consumed by Aevo Kiosk.",
            [
                Field("catalogChannel", "Default catalog channel", "The default channel used when the kiosk catalog opens.", "select", false, "KIOSK", options: Options(("KIOSK", "Kiosk"), ("QR", "QR ordering"), ("PICKUP", "Pickup"))),
                Field("allowPriceOverride", "Allow price overrides", "Permit an authorized operator to override a catalog price at the kiosk.", "boolean", false, false),
                Field("outOfStockMode", "Out-of-stock display", "Choose whether unavailable products are hidden or shown as sold out.", "select", false, "HIDE", options: Options(("HIDE", "Hide unavailable items"), ("SHOW_SOLD_OUT", "Show as sold out")))
            ]),
        new(
            "POS",
            "pos.orders.v1",
            "1",
            "Order defaults",
            "Store-level order handling defaults consumed by Aevo POS.",
            [
                Field("orderNumberPrefix", "Order number prefix", "Short prefix added to locally displayed order numbers.", "string", false, "ORD", maxLength: 8),
                Field("autoAcceptOrders", "Auto-accept orders", "Automatically accept incoming orders when the store is operating.", "boolean", false, true)
            ]),
        new(
            "QUEUE",
            "pos.queue.v1",
            "1",
            "Queue display behavior",
            "Store-level queue display defaults consumed by Aevo Queue.",
            [
                Field("displayMode", "Display mode", "Choose the primary queue presentation.", "select", false, "QUEUE", options: Options(("QUEUE", "Queue list"), ("BOARD", "Board"))),
                Field("autoAdvanceSeconds", "Auto-advance interval", "Seconds before a completed queue item advances automatically.", "integer", false, 30, min: 5, max: 300),
                Field("showCustomerName", "Show customer name", "Display customer names on the queue surface.", "boolean", false, false)
            ])
    ];

    public static bool TryGet(string applicationCode, string schemaRef, out HubApplicationConfigDefinition definition)
    {
        definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.ApplicationCode, applicationCode, StringComparison.Ordinal)
            && string.Equals(candidate.SchemaRef, schemaRef, StringComparison.Ordinal))!;
        return definition is not null;
    }

    public static IReadOnlyList<HubApplicationConfigDefinition> For(string applicationCode, IEnumerable<string> schemaRefs)
    {
        var normalized = applicationCode.Trim().ToUpperInvariant();
        return schemaRefs
            .Select(schemaRef => Definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.ApplicationCode, normalized, StringComparison.Ordinal)
                && string.Equals(candidate.SchemaRef, schemaRef, StringComparison.Ordinal)))
            .Where(candidate => candidate is not null)
            .Cast<HubApplicationConfigDefinition>()
            .ToArray();
    }

    public static JsonElement DefaultConfig(HubApplicationConfigDefinition definition)
    {
        var values = definition.Fields.ToDictionary(
            field => field.Key,
            field => JsonSerializer.SerializeToElement(field.DefaultValue));
        return JsonSerializer.SerializeToElement(values);
    }

    public static HubApplicationConfigSchemaRecord ToSchema(
        HubApplicationConfigDefinition definition,
        JsonElement config,
        DateTimeOffset? updatedAt) => new(
        definition.SchemaRef,
        definition.SchemaVersion,
        definition.Label,
        definition.Description,
        definition.Fields.Select(field => new HubApplicationConfigFieldRecord(
            field.Key,
            field.Label,
            field.Description,
            field.Type,
            field.Required,
            JsonSerializer.SerializeToElement(field.DefaultValue),
            field.Min,
            field.Max,
            field.MaxLength,
            field.Options)).ToArray(),
        config,
        updatedAt);

    private static HubApplicationConfigFieldDefinition Field(
        string key,
        string label,
        string description,
        string type,
        bool required,
        object defaultValue,
        int? min = null,
        int? max = null,
        int? maxLength = null,
        IReadOnlyList<HubApplicationConfigOptionRecord>? options = null) =>
        new(key, label, description, type, required, defaultValue, min, max, maxLength, options);

    private static HubApplicationConfigOptionRecord[] Options(params (string Value, string Label)[] options) =>
        options.Select(option => new HubApplicationConfigOptionRecord(option.Value, option.Label)).ToArray();
}

public sealed class HubApplicationConfigurationValidationException(string code, string message, int statusCode = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
