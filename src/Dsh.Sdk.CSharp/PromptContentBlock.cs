namespace DeepSeek.Harness.Sdk;

/// <summary>A text or inline raster image block accepted by the Harness SDK protocol.</summary>
public sealed record PromptContentBlock
{
    private PromptContentBlock(
        string type,
        string? text,
        string? data,
        string? mimeType,
        string? name)
    {
        Type = type;
        Text = text;
        Data = data;
        MimeType = mimeType;
        Name = name;
    }

    /// <summary>Wire discriminator.</summary>
    public string Type { get; }

    /// <summary>Text content for a text block.</summary>
    public string? Text { get; }

    /// <summary>Base64-encoded image bytes for an image block.</summary>
    public string? Data { get; }

    /// <summary>Image MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Optional display name; it is not interpreted as a filesystem path.</summary>
    public string? Name { get; }

    /// <summary>Creates a text block.</summary>
    public static PromptContentBlock FromText(string text) =>
        new("text", text ?? throw new ArgumentNullException(nameof(text)), null, null, null);

    /// <summary>Creates an inline raster image block.</summary>
    public static PromptContentBlock FromImage(PromptImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PromptContentBlock("image", null, image.Data, image.MimeType, image.Name);
    }
}
