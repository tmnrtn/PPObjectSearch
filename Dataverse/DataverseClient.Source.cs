using System.Text;

namespace PPObjectSearch.Dataverse;

/// <summary>A web resource's content, decoded where it is text.</summary>
public sealed record WebResourceContent(string Name, int Type, string TypeLabel, string? Text, int ByteCount)
{
    /// <summary>What Save As suggests - the web resource's own extension where it has one.</summary>
    public string FileExtension => Type switch
    {
        1 => ".html",
        2 => ".css",
        3 => ".js",
        4 => ".xml",
        9 => ".xsl",
        11 => ".svg",
        12 => ".resx",
        _ => string.Empty
    };
}

public sealed partial class DataverseClient
{
    /// <summary>webresource.webresourcetype values whose content is text worth showing.</summary>
    private static readonly HashSet<int> TextWebResourceTypes = new() { 1, 2, 3, 4, 9, 11, 12 };

    public static bool IsTextWebResourceType(int type) => TextWebResourceTypes.Contains(type);

    /// <summary>
    /// A web resource's content. Dataverse stores it base64-encoded; text types are decoded as UTF-8,
    /// images and other binaries report only their size.
    /// </summary>
    public async Task<WebResourceContent> GetWebResourceContentAsync(Guid webResourceId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + $"webresourceset({webResourceId})?$select=name,webresourcetype,content",
            ct, Annotations.Formatted).ConfigureAwait(false);

        var root = doc.RootElement;
        var type = JsonHelper.GetInt(root, "webresourcetype") ?? 0;
        var encoded = JsonHelper.GetString(root, "content");

        byte[] bytes;
        try
        {
            bytes = string.IsNullOrEmpty(encoded) ? Array.Empty<byte>() : Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new DataverseException("The web resource's content is not valid base64.");
        }

        string? text = null;
        if (IsTextWebResourceType(type))
        {
            // UTF-8 with or without a byte order mark; the decoder drops the mark either way.
            text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }

        return new WebResourceContent(
            JsonHelper.GetString(root, "name") ?? webResourceId.ToString(),
            type,
            JsonHelper.GetString(root, "webresourcetype@" + Annotations.Formatted) ?? $"Type {type}",
            text,
            bytes.Length);
    }

    /// <summary>
    /// A cloud flow's definition - workflow.clientdata, the JSON that the flow designer saves and
    /// that export packages carry as the flow's .json file. Null when the flow has none.
    /// </summary>
    public async Task<string?> GetCloudFlowDefinitionAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + $"workflows({workflowId})?$select=clientdata", ct).ConfigureAwait(false);

        return JsonHelper.GetString(doc.RootElement, "clientdata");
    }
}
